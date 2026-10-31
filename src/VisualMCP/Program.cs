using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using VisualMCP.Logging;
using VisualMCP.Startup;
using VisualMCP.Update;
using Snitch;
using Snitch.Languages.VB;

// Redirect stdout to null immediately: the MCP stdio transport uses the raw
// stdout stream directly, so Console.SetOut does not affect it, but it does
// prevent any stray text (host startup banners, log lines) from corrupting
// the JSON protocol before our FileLoggerProvider takes over.
Console.SetOut(TextWriter.Null);

// Must be called before any MSBuild/Roslyn types are JIT-compiled.
MSBuildLocator.RegisterDefaults();

await RunServerAsync(args);

// Separate method so the JIT doesn't compile Roslyn types before RegisterDefaults().
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static async Task RunServerAsync(string[] args)
{
    var logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "mcp-servers", "VisualMCP", "debug.log");

    const string ServerInstructions =
        "VisualMCP exposes Roslyn-powered, semantically-accurate analysis, navigation and " +
        "refactoring for C#/.NET solutions, plus Visual Basic style rewrites and a cross-language " +
        "pseudocode view (metacode_*).\n\n" +
        "WHEN TO USE: Whenever you are working on a .NET solution (.sln/.slnx), prefer these " +
        "tools over plain text search (grep) or reading files by hand. They use the compiler's " +
        "semantic model, so they correctly resolve overloads, overrides, interface members, " +
        "generics and partial types that text matching gets wrong.\n\n" +
        "ROUTING:\n" +
        "- Find where a symbol is used / who calls a method -> find_references, find_callers " +
        "(NOT grep — grep also hits comments, strings and unrelated same-named text).\n" +
        "- Locate a class/method/interface by name -> find_symbol.\n" +
        "- Read the SOURCE of a declaration (the body of a type/method/property) -> read_symbol. " +
        "This replaces search_text + read_file when you want the code of a name you just saw.\n" +
        "- Find implementations of an interface or derived types -> find_implementations, find_derived_types.\n" +
        "- Inspect a type's members or a symbol's signature/docs -> get_type_members, get_symbol_info.\n" +
        "- Dead code, code smells, complexity, metrics, security, DI, duplicate/async issues -> the Analysis tools " +
        "(reliable detection needs the control-flow graph, which only Roslyn can build — do NOT infer these by reading source).\n" +
        "- Why does a symbol look unused/dead? Three boundaries of a static call graph, each with a tool: reflective lookups by " +
        "name with the names that resolve to the solution -> find_reflection_usage; the members a framework calls by convention " +
        "(MVC actions, hub methods, hosted services, Blazor lifecycle, EF overrides, tests) -> framework_entry_points (and the " +
        "frameworkInvoked list find_unused_symbols now reports instead of calling them unused); source generators and the trees " +
        "they emit, which are in the compilation but not in project.Documents -> list_generated_sources. What a referenced " +
        "assembly's surface is actually used for -> find_external_usages. Whether a change altered BEHAVIOUR (tests run on the " +
        "working tree and on a git revision in a detached worktree, outcomes diffed per test) -> verify_change.\n" +
        "- Architecture and quality questions: are the layer rules respected (a dependency pointing back up, with file:line) -> " +
        "check_layer_violations; the dependency graph as a DOT/Mermaid diagram (projects, namespaces, TYPES) -> export_dependency_graph; is this class " +
        "really two classes (LCOM4) -> get_cohesion_metrics; test suite quality (no assert, empty, unreadable names, async void) -> " +
        "analyze_test_quality; public parameters that are never null-checked -> find_missing_validation; where nullable reference " +
        "types actually protect (suppressions, CS86xx) -> analyze_nullability; what a branch breaks in the public API -> " +
        "find_breaking_changes. Inspections judged against what the codebase already had (only new findings, for a CI gate) -> " +
        "inspection_baseline (create once, check after each change).\n" +
        "- Allocation anti-patterns you can read out of the code (string += in a loop, a LINQ materialiser inside a loop, a " +
        "collection allocated per iteration) -> find_performance_anti_patterns. It is a reading, not a measurement: when a " +
        "process is available, profile_hotpaths / profile_allocations say where the bytes actually go.\n" +
        "- Two different questions both called 'complexity': how many paths a reader has to hold (cyclomatic/cognitive — " +
        "complexity_report for one method in full with the lines that own the score, get_complexity_hotspots for the ranked " +
        "list) and how it scales with n (Big-O — analyze_complexity). To actually LOWER the first one, fix_complexity is the " +
        "one call: it ranks the solution, lowers the worst methods with reduce_complexity's moves (extract block, name the " +
        "boolean, guard clause), re-measures and reports before/after — use reduce_complexity only when you already know the " +
        "exact method. Both are compiler-gated and preview by default.\n" +
        "- See a shape instead of a list: the call graph of a method (Mermaid/DOT, incoming/outgoing, a depth) -> call_graph; " +
        "the STATIC call order as a Mermaid sequence diagram -> sequence_diagram; a method's control-flow graph (blocks, " +
        "branch conditions, try regions) -> control_flow_graph; where a value is written and read -> track_value; the " +
        "localization view (.resx keys nothing references, missing translations, user-facing literals) -> localization_report.\n" +
        "- Razor pages/views (.cshtml) and Blazor components (.razor): list and outline them (kind, @page route, @model, " +
        "layout, sections, @inject, handlers) -> razor_pages; markup/accessibility/maintainability problems with file:line " +
        "and a fix -> analyze_razor_pages; security audit (XSS, CSRF, secrets, CSP) with CWE ids -> find_razor_vulnerabilities. " +
        "These read the files from disk, so they also take an explicit path.\n" +
        "- Rename, extract, change signature, move type, etc. -> the Refactoring tools (semantic and safe, " +
        "they update every reference; do NOT hand-edit for these).\n" +
        "- Find or rewrite code by SHAPE, not by name (\"every call to X\", \"every guard that throws\") -> " +
        "structural_search: ordinary C# with $holes$ matched on the syntax tree; pass replacement to rewrite " +
        "(compiler-gated). Clean a file/project/solution in one call (style → usings → regions → reorder → format) -> " +
        "code_cleanup; formatting alone -> format_code. Create what the code already assumes -> implement_members " +
        "(interface/abstract stubs), create_from_usage (the member or type a call site needs), generate_docs (XML comments).\n" +
        "- The context actions that are neither style rewrites nor refactorings — import the using a type on a line needs, " +
        "generate argument guards (null / all parameters / string not null or empty / collection not empty / variable null), " +
        "wrap a statement range in try/catch, try/finally, if or using, convert a string literal between regular, verbatim " +
        "and raw -> context_action (action id + line; `text` carries the parameter, the condition or the resource; preview by " +
        "default, refused when the edit would add a compiler error).\n" +
        "- Apply Visual Basic style suggestions (15 rule ids, compiler-gated, preview by default) -> simplify_vb_code " +
        "(simplify_code rewrites C# only).\n" +
        "- Compiler errors/warnings for the whole solution -> get_diagnostics; to FIX the mechanical ones " +
        "(unused local/field/event, duplicated using, a member hiding an inherited one, a 'new' that hides " +
        "nothing) with a compiler gate and a revertible write -> fix_diagnostics.\n" +
        "- Where does a refactoring pay first? git history (how often a file changes) joined with Roslyn " +
        "complexity (how hard its worst method is to read) -> git_churn_hotspots (read-only; since/path/minCommits " +
        "narrow the window).\n" +
        "- Run the app or the tests -> run_project (launches 'dotnet run' with a timeout), run_tests.\n" +
        "- Debug a running program — a REAL debugger (DAP), not logging: install the adapter once -> debug_install " +
        "(debug_status says whether it is there; a few MB from the Samsung release); start a built .dll or a " +
        "project -> debug_launch (breakpoints can be set in the same call and the call waits for the first stop); " +
        "attach to an already-running pid -> debug_attach; set/replace one file's breakpoints -> debug_breakpoints " +
        "(reports verified vs unverified lines); advance -> debug_continue, debug_step (over|into|out), debug_pause; " +
        "read -> debug_stack (file:line), debug_variables (Locals/Arguments/Autos, depth for members), " +
        "debug_evaluate (watch expression), debug_threads, debug_output; break on exceptions -> debug_exceptions; " +
        "close -> debug_terminate; list live sessions -> debug_sessions. Sessions survive across calls, so a stop " +
        "that happened between two calls is still there.\n" +
        "- Run the configured Roslyn analyzers (StyleCop/Roslynator/.NET analyzers) and get their diagnostics -> run_code_analysis.\n" +
        "- Run BenchmarkDotNet benchmarks and get the summary table -> run_benchmarks; benchmark an arbitrary snippet without writing a [Benchmark] class -> quick_benchmark.\n" +
        "- Run a build/export/packaging script or any shell command — PowerShell, pwsh, cmd or bash (incl. nested shells, pipes, grep/sed, redirection, or commands too long for the normal shell) -> run_command.\n" +
        "- Profile a running .NET process (all native — do NOT shell out to dotnet-trace/counters/gcdump/dump/stack): " +
        "find the process -> profile_processes; collect an EventPipe trace -> profile_collect (cpu/gc/exceptions/threading presets); " +
        "turn it into CPU hotspots, allocation sites, GC pauses, exceptions, contention and JIT time with file:line -> profile_analyze; " +
        "attribute allocations to the allocating method/line -> profile_allocations; see inclusive/exclusive CPU per call path and export folded stacks -> profile_calltree; " +
        "find and rank multiple hot paths at once, with the method/line fix list and the branch points where cost splits -> profile_hotpaths; " +
        "the exact LINE inside a method that burns the CPU (self time per source line with the code text; method= to scope it) -> profile_hotlines; " +
        "find where threads block on I/O (file/http/socket/database/pipes, sync-over-async, locks, sleep) with duration and call site -> profile_io; " +
        "compare two runs and get regressions -> profile_compare; run counters + trace + stacks + heap in one call and get correlated findings -> profile_diagnose; " +
        "live EventCounters -> profile_counters; managed stacks of every thread + contended locks -> profile_stacks; " +
        "heap types, LOH, finalizable objects and duplicate strings -> profile_heap; who keeps a type alive " +
        "(root chains with the holding fields, plus the referrer types) -> profile_retention; collect or analyse a dump -> profile_dump; " +
        "download PDBs so frames resolve to file:line -> profile_symbols; check/install the external diagnostics tools -> profile_tools; " +
        "start/probe dotnet-monitor -> profile_monitor.\n" +
        "- Why is the process slow? A ranked verdict (cpu/gc/io-wait/sync-over-async/lock-wait/blocked-native, " +
        "each as the share of the window it explains) instead of a table -> profile_slowdown.\n" +
        "- Why is memory growing? growth/shrink/stable/single-snapshot, with the type correlated to its allocation " +
        "site when a gc trace is given -> profile_memory_growth.\n" +
        "- Is the thread pool starved? starved/at-risk/burst/healthy/inconclusive — it requires a full queue AND " +
        "threads that cannot take the work -> profile_threadpool.\n" +
        "- Is this change a regression? Per-second normalized rates with a significance floor, and a refusal when " +
        "the two windows are too short to compare -> profile_review_change.\n" +
        "- Free a locked build output / restart the server (e.g. a 'file in use' error when rebuilding) -> stop_server.\n" +
        "- Check the local toolchain (dotnet SDKs/runtimes, Visual Studio & MSVC cl.exe via vswhere, OS, and the size of given files/libraries) -> get_environment_info.\n" +
        "- Manage NuGet packages: versions & version conflicts -> list_nuget_packages; available upgrades -> check_nuget_updates; known CVEs/advisories -> check_nuget_vulnerabilities; find a package's DLLs in the local cache -> locate_nuget_package; search the nuget.org gallery for a package id by name/keywords -> search_nuget_packages; add/remove/update a package -> add_nuget_package / remove_nuget_package / update_nuget_package; auto-fix vulnerable packages -> fix_nuget_vulnerabilities; resolve a version conflict across projects -> unify_nuget_version.\n" +
        "- Inspect a compiled .NET assembly's public API (types, members, signatures) without running it -> inspect_assembly (great with locate_nuget_package for third-party DLLs); SEVERAL assemblies in one call -> inspect_assemblies; " +
        "find TYPES OR MEMBERS BY NAME inside one assembly with a regex (a type name returns its members, a member name returns just the matching signatures) -> find_assembly_api — use it INSTEAD OF calling inspect_assembly once per typeFilter.\n" +
        "- Decompile a compiled .NET assembly/type back to C# (ILSpy engine) -> decompile_type (one type) / decompile_assembly (whole module) — read the real implementation of a library; several types of one assembly in one call -> decompile_types. " +
        "To WORK inside a decompiled assembly (search its types/members with CamelHumps, read a member's source, find its uses inside the assembly, outline it) -> assembly_workspace (load once, keyed on the module version id). " +
        "The C# in the answer is compacted (indentation, line breaks and comments dropped) unless compact=false; a file written to outputPath keeps the readable formatting and is searchable with text_index afterwards.\n" +
        "- Who calls this and what it calls, more than one level deep -> call_hierarchy (incoming + outgoing tree with a depth); find_callers stays the one-level view.\n" +
        "- Decompile an assembly into CANONICAL PSEUDOCODE (ILSpy → C# → the metacode model: one type, or a paged set of types) -> decompile_to_metacode; to KEEP what you decompile, store every type as a metacode tree in the solution database -> metacoding action=decompile. That same action ROUTES a native binary (no CLR metadata) to Ghidra and stores the recovered functions in the solution database's native_artifact / native_function / native_call tables, keyed on the file's SHA-256.\n" +
        "- Recover a NATIVE binary's functions with Ghidra headless and print them as pseudocode (a real external run, tens of seconds; Ghidra is installed on demand into the per-user tools folder; the run is also persisted in native_artifact / native_function / native_call; re-read an existing snapshot with metacode_translate) -> analyze_native_binary.\n" +
        "- Explore a NuGet package directly by id+version (auto-locates its DLL): public API -> inspect_package; a type's C# source -> decompile_package_type; SEVERAL packages in one call -> inspect_packages.\n" +
        "- Read code in the canonical pseudocode (C# source, a Razor page, a Ghidra snapshot or pseudocode itself; a " +
        "construct with no node stays visible as RAW + a diagnostic) -> metacode_translate (no solution needed).\n" +
        "- Check that two spellings of the same algorithm really are the same -> metacode_compare (translates both " +
        "sides, then diffs the canonical text line by line; any mix of C#/Razor/Ghidra/pseudocode).\n" +
        "- Read a PYTHON or C/C++ file in that same pseudocode (the dump is produced for you: CPython's ast via " +
        "translate_python_file, clang -ast-dump=json via translate_cpp_file; pass dumpJson to translate a dump produced elsewhere).\n" +
        "- The SOLUTION'S metacode, persisted in .visualMCP/visualmcp.db and generated when the solution opens " +
        "(the background pass keeps it in step with saved files and a build refreshes it) -> metacode_index: " +
        "action=status|build|complexity|trees. Every source of the solution is stored twice there — its native text " +
        "and language (source_document) and its metacode (mc_document/mc_tree); the document set is decided by " +
        "language, so Razor pages/components are included and a language no frontend reads (Visual Basic today) is " +
        "recorded natively and reported as `unsupported` instead of being skipped. Its complexity is computed over " +
        "the language-neutral model " +
        "(cyclomatic + cognitive); complexity_report remains the C#/Roslyn view with Halstead and the maintainability index. " +
        "Generate new C# from metacode (pseudocode text, a stored symbol, or a file) -> metacode_generate; every " +
        "construct C# cannot express is lowered with a diagnostic instead of being dropped. To WRITE through the model " +
        "(decompile a managed OR native binary INTO the store, replace a declaration from pseudocode, add a generated " +
        "member) -> metacoding. " +
        "Mechanical style improvements stay with simplify_code / code_cleanup / the Refactoring tools, not metacoding.\n" +
        "- Razor pages (.cshtml) and Blazor components (.razor): what exists and how it is wired -> razor_pages; " +
        "markup, accessibility and maintainability problems -> analyze_razor_pages; security (XSS, CSRF, secrets, CSP) " +
        "-> find_razor_vulnerabilities; edit one page (set/remove an attribute on the nth element, add a directive, " +
        "insert/replace/delete lines — verified with `expect`, previewed by default, revertible) -> razor_edit.\n" +
        "- Read a Razor page in the canonical pseudocode (a page becomes a class: @model/@inject are properties, " +
        "@code members are lowered as C#, the markup is the body of a Render method) -> metacode_translate on a " +
        ".cshtml/.razor path.\n" +
        "- XAML (.xaml/.axaml: WPF, WinUI, MAUI, Avalonia): what exists and how it is wired -> xaml_pages; markup " +
        "problems with rule ids and file:line (duplicate x:Name/x:Key, missing or unresolvable x:Class, an event " +
        "handler with no method, a resource key nothing defines, a DataTemplate without DataType, a name colliding " +
        "with a member, invalid XML) -> analyze_xaml; follow an x:Name, a resource key, a style (BasedOn chain) or " +
        "an event handler to everything attached to it -> xaml_navigate. Markup-only: bindings are not compiled.\n" +
        "- EF Core migrations through the project's own tool (list/add/script/remove/update/info) with a structured " +
        "result -> ef_migrations.\n" +
        "- Administer the inspections: set a diagnostic's severity in the right .editorconfig (solution, project or " +
        "one file) -> configure_inspection; silence one finding with a #pragma around the member or a SuppressMessage " +
        "attribute on it (compiler-gated) -> suppress_finding.\n" +
        "- Inspections the repository owns rather than an analyzer: declarative rules (banned symbols/namespaces/" +
        "attributes, identifier and comment patterns) in .visualMCP/inspections.json -> custom_inspections; the " +
        "ReSharper .DotSettings a team shares (severities, disabled ids, custom dictionary words) -> read_dot_settings; " +
        "likely typos (one edit from a word the solution or the dictionary uses) -> find_spelling_issues; the " +
        ".editorconfig naming policy, list or rename the violations -> naming_rules; and the warm loop that re-runs the " +
        "packs when the solution changes, with cached findings to poll -> background_analysis / analysis_findings.\n" +
        "- ReSharper's proprietary inspection ids (~2500, closed-source PSI analyzers): what each maps to here and what " +
        "is absent -> resharper_inspection_map; 8 of them are implemented natively under ReSharper's ids in the opt-in " +
        "'rider' inspection pack (inspect_code packs=rider).\n" +
        "- Test workflow: run ONE test or class without hand-building --filter, with a TRX result and the failures " +
        "recorded -> run_test; re-run exactly those failures -> rerun_failed; which test methods reference this symbol " +
        "-> tests_covering_symbol; scaffold an xUnit class for a type (one [Fact] per public method, compiler-gated) -> " +
        "generate_tests; what the suite does NOT catch -> run_mutation_tests (Stryker.NET: mutation score, survivors with file and line). " +
        "Coverage maps now include branch coverage.\n" +
        "- The rest of the test workflow: discover the tests and their inline data rows without running -> list_tests; " +
        "debug ONE test (VSTest host under the debugger, breakpoints set before resuming) -> debug_test; run exactly the " +
        "tests a change affects -> run_affected_tests; run again on every save -> continuous_testing; find the tests " +
        "whose outcome changes between runs -> find_flaky_tests.\n" +
        "- Add/remove projects to/from the solution (.sln/.slnx) -> add_projects_to_solution / remove_projects_from_solution.\n" +
        "- Modernize the solution: add Aspire (status, install the templates if missing, scaffold AppHost + ServiceDefaults, wire and verify) -> aspire; move the projects to a newer target framework with a build gate and rollback -> update_framework; upgrade every outdated NuGet package (Central Package Management aware), optionally with the framework bump, in one gated pass -> update_solution. Prefer update_solution over one update_nuget_package call per package when modernising a whole solution.\n" +
        "- Git: inspect state -> git_status / git_log / git_diff; stage -> git_stage; commit -> git_commit; new branch -> git_create_branch. (No push/force — use run_command for those.)\n" +
        "- Check if the solution/project compiles and get structured errors/warnings -> build_project " +
        "(runs 'dotnet build', works even while the app is running).\n" +
        "- Extract errors from a native/C++ or MSBuild log file (handles UTF-16) -> extract_build_log_errors.\n" +
        "- Check Docker engine availability and running containers -> docker_status.\n" +
        "- List a folder's contents/sizes (build output, repo root, a library dir) -> list_directory (read-only; filter by glob/name, optional recurse).\n" +
        "- Regex-search text across files (C/C++ headers, configs, logs — any language) -> search_text (use INSTEAD OF 'cd … && grep'; for C# symbols prefer find_symbol/find_references).\n" +
        "- Find code AND read it in one call: a regex whose every hit comes back as the whole declaration that contains " +
        "it (method/property/type with its XML docs; a line window for files outside the solution) -> search_code. " +
        "Prefer it to search_text followed by read_file/read_files, which is the sequence it replaces.\n" +
        "- Find where something is USED, NAMED or DOCUMENTED anywhere in the solution — source, Markdown, configs, " +
        "decompiled assemblies — by words rather than by symbol -> text_index (action=search): one call answers " +
        "file:line + the matching line + the files that match most, with words matched inside identifiers " +
        "('required' finds GetRequiredService). Prefer it to search_text + read_file when you do not need a regex; " +
        "it is backed by an FTS5 index in .visualMCP/visualmcp.db (built on demand, action=build to refresh).\n" +
        "- Download a file from an http/https URL (dataset, asset, artifact) -> download_file (INSTEAD OF Invoke-WebRequest/curl).\n" +
        "- Move or rename a file/directory -> move_path (INSTEAD OF shell mv/Move-Item/git mv). To rename a C# symbol/type/namespace and update all references, use apply_rename (semantic).\n" +
        "- Persistent project memory belongs to the CLIENT, not to this server: recall and save it with the host's own " +
        "memory mechanism. This build exposes NO memory_read, memory_write or memory_append — if a stale build, an old " +
        "transcript or another server mentions them, do not call them. Notes another client keeps in " +
        "~/.claude/projects/<slug>/memory are carried into this client's store at startup, and read_project_docs " +
        "reports what moved. For facts the repository documents itself (README, docs, " +
        "CONTRIBUTING), read_project_docs is the right source and is indexed automatically.\n" +
        "- Interact with GitHub (create repo, PRs, issues, releases, runs) -> github (authenticated 'gh' CLI passthrough).\n" +
        "- Update VisualMCP itself from GitHub Releases: see what's available -> check_for_update; install the latest -> self_update.\n" +
        "- Scaffold project metadata: a LICENSE from a bundled template (20 licenses — call list_licenses for the catalogue with SPDX ids, copyleft family, OSI approval and patent grants) -> add_license; CONTRIBUTING + CLA -> add_contributing; a multi-OS GitHub Actions build (workflowType 'dotnet-multi-os') or a multi-OS release that publishes binaries to a GitHub Release on a tag ('dotnet-release') -> add_ci_workflow.\n" +
        "- Call a REST API endpoint of a running app -> http_invoke (any HTTP method, custom headers, JSON body); " +
        "run a whole REST-client request file (.http/.rest with @variables and ### separators) -> run_http_file.\n" +
        "- Test a SignalR hub (connect, subscribe, invoke, drain events) -> signal_r_connect / signal_r_subscribe / signal_r_invoke / signal_r_events / signal_r_disconnect.\n" +
        "- Run many mechanical steps at once — the same call over a list of files or projects, then a build — -> run_plan: a dependency graph of 'tool' steps (any tool listed here, by name) and shell 'command' steps, executed natively in parallel, validated before it starts, answered in one round trip. Prefer it to issuing the same tool call N times, or delegating a mechanical fan-out to subagents; it is NOT for steps that need judgement between them.\n\n" +
        "WORKFLOW:\n" +
        "- When you START working on a solution, first call read_project_docs to read its README/Markdown/" +
        "docs (indexed automatically when the solution opens) and understand the project before reading code.\n" +
        "- When a task has MULTIPLE steps, call create_task_checklist at the start to write a Markdown " +
        "Task|Done table, then update_task_checklist to tick steps off as you complete them.\n" +
        "- After building, testing, or implementing/changing code, run run_code_analysis (or rely on " +
        "build_project's automatic analyzer pass) to catch analyzer/code-style issues before moving on.\n" +
        "- When you change performance-sensitive code, measure it with quick_benchmark (ad-hoc, no [Benchmark] " +
        "attribute needed) or run_benchmarks for existing BenchmarkDotNet projects.\n\n" +
        "SETUP: The solution in the working directory is auto-discovered and loaded on demand, so you can " +
        "call any tool directly. Only call load_solution if a tool reports that no solution could be located, " +
        "or to target a specific .sln/.slnx by path. Use list_analysis_tools for the full catalogue.";

    // stdin wrapper: the eager load below uses it to tell a real client (initialize)
    // from the discovery probe ZCode spawns per session (server/discover + tools/list,
    // then gone). Without that distinction the probe loads every solution too.
    var stdin = new ProbeDetectingStream(Console.OpenStandardInput());

    using var host = Host.CreateDefaultBuilder(args)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new FileLoggerProvider(logPath));
            logging.SetMinimumLevel(LogLevel.Debug);
        })
        .ConfigureServices(services =>
        {
            // Snitch (the code-intelligence library) configured with VisualMCP's identity.
            services.AddSnitch(o =>
            {
                o.ApplicationName = "VisualMCP";
                o.ProcessName     = "VisualMCP";
                o.Download.DefaultUserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VisualMCP/1.0";
            });

            // Metacode: the translator plus the ILSpy and Ghidra decompilers. Registered here because
            // decompile_type/decompile_assembly decompile_package_type/inspect_package resolve
            // IDecompileService and IPackageInspectionService, which AddSnitch deliberately does not.
            services.AddSnitchMetacode();

            // Self-update is VisualMCP's own deployment concern, not part of the
            // reusable Snitch library — registered here in the plugin.
            services.AddSingleton<ISelfUpdateService, SelfUpdateService>();

            // Python/C++ reachability: the two opt-in metacode readers need an AST dump, and this is
            // what produces one (python -m ast / clang -ast-dump=json) or accepts one from the caller.
            services.AddSingleton<VisualMCP.Translation.ISourceDumpTranslationService,
                                  VisualMCP.Translation.SourceDumpTranslationService>();

            // Visual Basic style layer, opt-in by design: AddSnitch stays C#-only so the container never
            // holds two ISimplifyCodeService instances. simplify_vb_code resolves IVbSimplifyCodeService
            // and answers with a message (not a DI exception) if this call is ever removed.
            services.AddSnitchVisualBasic();

            // Profiling: native EventPipe collectors, ClrMD snapshots, trace analysis and the
            // installer for the external dotnet diagnostics toolchain.
            services.AddVisualMcpProfiling();

            // The debugger: DAP sessions over netcoredbg (installed on demand like the diagnostics
            // tools), with breakpoints, stepping, stacks, variables and expression evaluation.
            services.AddSnitchDebugging();

            // The XAML layer (.xaml/.axaml): inventory, markup diagnostics with Roslyn-resolved
            // code-behind, and navigation from a name/key/handler to everything attached to it.
            services.AddSingleton<Snitch.Languages.Xaml.IXamlPagesService,
                                  Snitch.Languages.Xaml.XamlPagesService>();
            services.AddSingleton<Snitch.Languages.Xaml.IAnalyzeXamlService,
                                  Snitch.Languages.Xaml.AnalyzeXamlService>();
            services.AddSingleton<Snitch.Languages.Xaml.IXamlNavigationService,
                                  Snitch.Languages.Xaml.XamlNavigationService>();

            // The EF Core migration workflow ('dotnet ef' with a structured result).
            services.AddSingleton<Snitch.Execution.IEfMigrationsService, Snitch.Execution.EfMigrationsService>();

            // VisualMCP-side inspection services: the packs ship analyzers *and* the code fixes,
            // and these two wrappers are injected into inspect_code / apply_code_fix.
            services.AddSingleton<Snitch.Languages.CSharp.Inspections.IInspectCodeService,
                                  Snitch.Languages.CSharp.Inspections.InspectCodeService>();
            services.AddSingleton<Snitch.Languages.CSharp.Inspections.ICodeFixService,
                                  Snitch.Languages.CSharp.Inspections.CodeFixService>();
            // Baseline for a CI gate: the findings the codebase already has, so a later run reports only
            // the ones the change introduced.
            services.AddSingleton<Snitch.Languages.CSharp.Inspections.IInspectionBaselineService,
                                  Snitch.Languages.CSharp.Inspections.InspectionBaselineService>();
            // The warm inspection loop needs the real IInspectCodeService, which lives in this host:
            // it re-runs the packs when the solution changes and caches the findings for analysis_findings.
            services.AddSingleton<Snitch.Languages.CSharp.Inspections.IBackgroundAnalysisService,
                                  Snitch.Languages.CSharp.Inspections.BackgroundAnalysisService>();

            // Administering the inspections: severity in .editorconfig and compiler-gated suppressions.
            services.AddSingleton<Snitch.Languages.CSharp.Inspections.IInspectionConfigurationService,
                                  Snitch.Languages.CSharp.Inspections.InspectionConfigurationService>();

            // The test workflow: one test / re-run the failures (TRX + a record), which tests reach a
            // symbol, and a compile-gated test class scaffold.
            services.AddSingleton<Snitch.Languages.CSharp.Testing.ITestWorkflowService,
                                  Snitch.Languages.CSharp.Testing.TestWorkflowService>();
            // Running the tests a change affects: it composes test_impact_analysis with the run.
            services.AddSingleton<Snitch.Languages.CSharp.Testing.IAffectedTestsService,
                                  Snitch.Languages.CSharp.Testing.AffectedTestsService>();

            // The call hierarchy in both directions (find_callers is one level).
            services.AddSingleton<Snitch.Languages.CSharp.Navigation.ICallHierarchyService,
                                  Snitch.Languages.CSharp.Navigation.CallHierarchyService>();

            // The refactoring batch: inline a field, extension → static, safe delete of files/folders,
            // members to a partial file, invert a boolean (all compiler-gated, preview by default).
            services.AddSingleton<Snitch.Languages.CSharp.Refactoring.IInlineFieldService,
                                  Snitch.Languages.CSharp.Refactoring.InlineFieldService>();
            services.AddSingleton<Snitch.Languages.CSharp.Refactoring.IExtensionToStaticService,
                                  Snitch.Languages.CSharp.Refactoring.ExtensionToStaticService>();
            services.AddSingleton<Snitch.Languages.CSharp.Refactoring.ISafeDeleteFileService,
                                  Snitch.Languages.CSharp.Refactoring.SafeDeleteFileService>();
            services.AddSingleton<Snitch.Languages.CSharp.Refactoring.IExtractMembersToPartialService,
                                  Snitch.Languages.CSharp.Refactoring.ExtractMembersToPartialService>();
            services.AddSingleton<Snitch.Languages.CSharp.Refactoring.IInvertBooleanService,
                                  Snitch.Languages.CSharp.Refactoring.InvertBooleanService>();

            // The map of the codebase (projects, areas, entry points, docs) and the map of the SERVER
            // (tools by area, rule catalogues, language layers) — the two answers an agent needs first.
            services.AddSingleton<Snitch.Languages.CSharp.Navigation.ISolutionMapService,
                                  Snitch.Languages.CSharp.Navigation.SolutionMapService>();
            services.AddSingleton<VisualMCP.Analysis.ICapabilityReportService,
                                  VisualMCP.Analysis.CapabilityReportService>();

            // Packaging: publish, project references, NuGet pack/push.
            services.AddSingleton<Snitch.Execution.IBuildPackagingService, Snitch.Execution.BuildPackagingService>();

            // The exploration windows as data: endpoints, TODO/FIXME, fuzzy file search, stack traces.
            services.AddSingleton<Snitch.Languages.CSharp.Navigation.IWorkspaceExplorationService,
                                  Snitch.Languages.CSharp.Navigation.WorkspaceExplorationService>();

            // Generators: a DTO from a JSON sample, a fluent builder (both compiler-gated).
            services.AddSingleton<Snitch.Languages.CSharp.Generation.IDtoFromJsonService,
                                  Snitch.Languages.CSharp.Generation.DtoFromJsonService>();
            services.AddSingleton<Snitch.Languages.CSharp.Generation.IBuilderGeneratorService,
                                  Snitch.Languages.CSharp.Generation.BuilderGeneratorService>();

            // The IL view of a compiled assembly (the C# ↔ IL toggle of the decompiled workspace).
            services.AddSingleton<Snitch.Metacode.Decompilation.IDisassemblyService,
                                  Snitch.Metacode.Decompilation.DisassemblyService>();

            // The refactoring added with the last batch: the inverse of add_params.
            services.AddSingleton<Snitch.Languages.CSharp.Refactoring.IRemoveParamsModifierService,
                                  Snitch.Languages.CSharp.Refactoring.RemoveParamsModifierService>();

            // Decompiled code as a place to navigate: one type per Roslyn document in an AdhocWorkspace,
            // keyed on the module version id, so search/source/references/outline answer without decompiling again.
            services.AddSingleton<Snitch.Metacode.Decompilation.IAssemblyWorkspaceService,
                                  Snitch.Metacode.Decompilation.AssemblyWorkspaceService>();

            // Whole-solution audit: composes the packs above with compiler diagnostics and metrics.
            services.AddSingleton<VisualMCP.Analysis.ISolutionAuditService, VisualMCP.Analysis.SolutionAuditService>();

            // Memory belongs to the client, and each client keeps it in its own store: notes under
            // ~/.claude/projects/<slug>/memory are carried into the one this client reads, so switching
            // client does not strand them and no memory_* tool has to be called for them.
            services.AddSingleton(sp => MemoryImportOptions.FromEnvironment(
                sp.GetRequiredService<SnitchContext>().WorkingDirectory));
            services.AddSingleton<IMemoryImporter, MemoryImporter>();

            // The plan executor behind run_plan: a DAG of steps — shell commands and calls to this
            // server's own tools — run concurrently in-process, with no model in the loop.
            services.AddSingleton<VisualMCP.Execution.IPlanCommandRunner, VisualMCP.Execution.PlanCommandRunner>();
            services.AddSingleton<VisualMCP.Execution.IPlanToolInvoker, VisualMCP.Execution.McpToolInvoker>();
            services.AddSingleton<VisualMCP.Execution.IPlanExecutionService, VisualMCP.Execution.PlanExecutionService>();

            // The web dashboard (Dashboard/): a read-only window on this process and the solution
            // store, on http://127.0.0.1:4677 — or the port it last used, or a free one.
            services.AddSingleton(VisualMCP.Dashboard.DashboardOptions.FromEnvironment(logPath));
            services.AddSingleton<VisualMCP.Dashboard.DashboardDatabase>();
            services.AddSingleton<VisualMCP.Dashboard.DashboardServer>();

            services.AddMcpServer(options =>
                {
                    options.ServerInstructions = ServerInstructions;
                })
                .WithStreamServerTransport(stdin, Console.OpenStandardOutput())
                .WithToolsFromAssembly()
                .WithToolCallLogging()
                .WithRequestLogging();
        })
        .Build();

    var logger = host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("VisualMCP.Startup");

    // Stop the dashboard when the host shuts down; it is bound to no lifetime of its own.
    var dashboard = host.Services.GetRequiredService<VisualMCP.Dashboard.DashboardServer>();
    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
    {
        try { dashboard.StopAsync().GetAwaiter().GetResult(); }
        catch { /* the process is going away */ }
    });

    // Eagerly open the environment in the background — discover and load every solution
    // under the working directory so they are usually ready by the first tool call. Tools
    // also open on demand, so this is a latency optimisation, not a correctness requirement.
    _ = Task.Run(async () =>
    {
        try
        {
            // Wait for the client to identify itself (usually a few milliseconds) before
            // paying for the load, so a discovery probe can be skipped entirely.
            for (var waited = 0; waited < 5000 && !stdin.FirstMessageSeen; waited += 25)
                await Task.Delay(25);

            if (stdin.IsDiscoveryProbe)
            {
                logger.LogInformation(
                    "Discovery probe detected (server/discover): skipping the eager solution load and the dashboard.");
                return;
            }

            // The dashboard binds before the load (it is independent of it) and never blocks the
            // MCP transport: a port that cannot be bound is logged and nothing else changes.
            await dashboard.StartAsync();

            // Carry the project memory that sits in another client's store into the one this client reads.
            // It needs no solution, so it runs before the "nothing discovered" exit below.
            try
            {
                var memory = await host.Services.GetRequiredService<IMemoryImporter>().RunAsync();
                if (memory.HasSources)
                {
                    logger.LogInformation(
                        "Memory import: {Sources} source(s), {Imported} note(s) carried over, {Present} already present, " +
                        "{Index} index line(s) added, target {Target}.",
                        memory.Sources.Count, memory.Imported.Count, memory.AlreadyPresent.Count,
                        memory.IndexLinesAdded, memory.Target ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The memory import failed; the environment load continues.");
            }

            var context = host.Services.GetRequiredService<SnitchContext>();
            var solutions = await context.OpenEnvironmentAsync();
            if (solutions.Count == 0)
            {
                logger.LogInformation(
                    "No solution auto-discovered from working directory '{Cwd}'. " +
                    "Tools will prompt for load_solution.", Directory.GetCurrentDirectory());
                return;
            }

            var loaded = solutions.Count(s => s.IsLoaded);
            logger.LogInformation(
                "Opened environment: {Loaded}/{Total} solution(s) loaded. Active: {Active}.",
                loaded, solutions.Count, context.ActiveSolution?.Name ?? "(none)");

            // Inspecting the solution means recording it twice, per source: the file as its own language
            // writes it, and the metacode Snitch's model makes of it. The metacode pass is incremental
            // (timestamp first, then content hash), so the background watcher reads nothing while nobody
            // edits; the native half of each document is written by that same pass, in the same transaction.
            var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            var metacode = host.Services.GetRequiredService<Snitch.Metacode.Indexing.IMetacodeIndexService>();

            try
            {
                var report = await metacode.BuildAsync(cancellationToken: stopping);
                logger.LogInformation(
                    "Solution inspection: {Sources} source(s), {Indexed} translated, {Unsupported} stored natively " +
                    "only (no frontend reads the language), {Skipped} unchanged, {Declarations} declaration(s), " +
                    "{Milliseconds} ms, database {Database}.",
                    report.Sources, report.Indexed, report.Unsupported, report.Skipped, report.Declarations,
                    report.Milliseconds, report.Database);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Metacode warm-up failed; the index builds on demand.");
            }

            metacode.StartWatching(stopping);

            // The native side of the same record, in the sense Roslyn means it: the semantic facts it
            // derives from those documents — declarations, uses, call and override relationships, and the
            // metacode of each declaration — persisted in the same database. It costs a compilation, so it
            // runs once here and again only when a tool asks (reference_index action=build).
            try
            {
                var symbols = host.Services.GetRequiredService<Snitch.Metacode.Symbols.ISymbolIndexService>();
                var built = await symbols.BuildAsync(cancellationToken: stopping);
                logger.LogInformation(
                    "Symbol index (native side): {Report}", System.Text.Json.JsonSerializer.Serialize(built));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The symbol-index warm-up failed; reference_index builds it on demand.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background environment open failed; tools will retry on demand.");
        }
    });

    await host.RunAsync();
}
