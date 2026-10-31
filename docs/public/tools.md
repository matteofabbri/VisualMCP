[Docs home](README.md) › **Tool catalogue**

# Tool catalogue

VisualMCP exposes **387 tools**. This page lists every one of them, grouped by what you are trying to
do. It is a map, not the full schema: each tool's exact arguments, defaults and required fields are in
the client (and live at `http://127.0.0.1:4677/tools` while the server runs).

Conventions:

- Names are snake_case. In a client they are prefixed, e.g. `mcp__VisualMCP__find_symbol`.
- "Solution tools" auto-discover and load the solution in the working directory. `load_solution` is only
  needed to target a different `.sln`/`.slnx`.
- A **✎** marks a tool that writes or changes state; **✎ preview** means it defaults to a dry run. Tables
  made of actions (refactoring, Git, Docker, Kubernetes, Node) are mostly writes; the read-only exceptions
  are named in the section.

## Contents

[Solution and projects](#solution-and-projects) · [Files and text](#files-and-text) ·
[Navigation](#navigation) · [Documentation](#documentation) · [Analysis](#analysis) ·
[Refactoring](#refactoring) · [Code style](#code-style) · [Build, run and test](#build-run-and-test) ·
[Debugging](#debugging) · [Benchmarks](#benchmarks) · [Profiling](#profiling) ·
[NuGet and packages](#nuget-and-packages) · [Modernization](#modernization) ·
[EF Core](#ef-core) · [Razor and Blazor](#razor-and-blazor) · [XAML](#xaml) · [SQL](#sql) ·
[Decompilation and reflection](#decompilation-and-reflection) · [Metacode](#metacode) ·
[Git](#git) · [GitHub](#github) · [Docker](#docker) · [Kubernetes and Helm](#kubernetes-and-helm) ·
[Node and JavaScript/TypeScript](#node-and-javascripttypescript) · [Planning](#planning) ·
[Automation and protocols](#automation-and-protocols) · [Maintenance](#maintenance) ·
[Scaffolding](#scaffolding) · [Self-management](#self-management)

## Solution and projects

| Tool | What it does |
|---|---|
| `list_solutions` | Scan a directory tree for `.sln`/`.slnx` files |
| `load_solution` | Load a specific solution by path, or reload the current one |
| `check_solution` | Report whether a solution is loaded, its path, project count and language |
| `get_project_info` | Source files, project/assembly references, NuGet packages and diagnostics for one project |
| `add_projects_to_solution` | ✎ Add one or more `.csproj` files to a `.sln`/`.slnx` and reload the workspace |
| `remove_projects_from_solution` | ✎ Detach projects from the solution without deleting their files |

## Files and text

| Tool | What it does |
|---|---|
| `read_file` | Read a file with line numbers, optionally a line range |
| `read_files` | Read several files in one call, with a total output budget and per-file errors |
| `read_tree` | An indented directory tree to a chosen depth, with sizes and counts; skips `bin`/`obj`/`.git` |
| `list_directory` | One folder's inventory with sizes and timestamps; glob and name filters |
| `move_path` | ✎ Move or rename a file or directory (physical move, not a symbol rename) |
| `search_text` | Regex search across any files, with `file:line` and optional context |
| `search_code` | The same regex, but every hit is resolved to the whole declaration that contains it (the search-then-read sequence in one call) |
| `text_index` | Full-text index of the solution (FTS5): search, build, status, files; identifiers are split into words, BM25 ranking |
| `download_file` | Download an http/https URL and save it or preview it |

## Navigation

| Tool | What it does |
|---|---|
| `find_symbol` | Find declared types and members by name, semantically; ranking exact → prefix → CamelHumps (`CPS` finds `CheckParameterSafety`) → contains, with the match kind on each hit, and a fuzzy-subsequence fallback (`custord` finds `CustomerOrder`) when nothing matched |
| `call_hierarchy` | The call hierarchy in both directions as a tree: who calls this member, what it calls, with a depth and a node budget |
| `solution_map` | The map of the codebase: projects and what each is, the namespaces with the largest types, the entry points, and the repository's Markdown |
| `capability_report` | What the SERVER can do, read from the running build: tools by area with live counts, rule catalogues, language layers |
| `find_file` | Find a file by name with exact → prefix → CamelHumps → contains ranking |
| `find_endpoints` | ASP.NET endpoints: controller actions and minimal-API Map* calls with verb, route and file:line |
| `find_todos` | The TODO / FIXME / HACK / XXX comments with file:line, grouped by marker |
| `analyze_stack_trace` | A stack trace as frames with file:line, resolving the frames that carry no location |
| `structural_search` | ✎ preview Match code by shape with `$holes$` (not regex), in C# or Visual Basic (`language=auto|cs|vb`), and report what each hole bound; with a replacement, rewrite every match under the compiler gate |
| `read_symbol` | Return the source of a declaration (whole type, method, property, field) with line numbers |
| `find_references` | Every real use of a symbol — reads, writes, calls — not just textual occurrences |
| `find_callers` | Call hierarchy: every method that calls the given method |
| `find_implementations` | Every type implementing an interface, with the member that satisfies each contract |
| `find_derived_types` | The downward inheritance tree of a class or interface |
| `get_symbol_info` | Resolve the symbol at a file and line (the IDE hover) |
| `get_type_members` | All members of a type with resolved signatures and XML docs, optionally inherited |

## Documentation

| Tool | What it does |
|---|---|
| `get_xml_docs` | A symbol's XML documentation, parsed into summary, params, returns, exceptions, remarks |
| `find_undocumented_public_api` | Public/protected types and members that lack XML docs |
| `read_project_docs` | The solution's README and Markdown docs, indexed when the solution opened; the first call of a session also reports project memory carried over from another client's store |

## Analysis

| Tool | What it does |
|---|---|
| `get_diagnostics` | Compiler errors and warnings (the IDE Error List), whole solution or one project |
| `fix_diagnostics` | ✎ preview Fix the mechanical compiler diagnostics instead of reporting them: CS0168/CS0219 unused local, CS0169 unused field, CS0067 unused event, CS0105 duplicated using, CS0108/CS0114 hidden member (`new`), CS0109 redundant `new`. Compiler-gated, recorded and revertible |
| `run_code_analysis` | Run the project's configured Roslyn analyzers (StyleCop, Roslynator, .NET analyzers) |
| `solution_audit` | One-call health report: inspections by id, compiler diagnostics, complexity hotspots, largest files, project graph and cycles, first moves; cached per unchanged solution |
| `inspect_code` | Built-in Rider-style inspection packs over every project, plus the VisualMCP house rules (`VMC0001`: one type per file) and the opt-in `rider` pack (ReSharper-shaped ids); filter by pack, id, severity, file |
| `apply_code_fix` | ✎ preview Apply a diagnostic's own code fix, optionally FixAll at document/project/solution scope |
| `inspection_baseline` | ✎ create/check/show/clear a baseline of `inspect_code` findings, so a change is judged only by the findings it introduced |
| `configure_inspection` | ✎ Set `dotnet_diagnostic.<id>.severity` (none/silent/suggestion/warning/error/default) in the right `.editorconfig`: solution, project or one file. Idempotent; reports the previous value |
| `configure_formatting` | ✎ Set any other `.editorconfig` option (`csharp_*`, `dotnet_*`, `indent_*`, `end_of_line`, `charset`, `file_header_template`, …) at the same scopes, same upsert |
| `suppress_finding` | ✎ preview Silence one finding where it takes effect: a `#pragma` around the member or a `SuppressMessage` attribute on it; compiler-gated |
| `custom_inspections` | Inspections declared in `.visualMCP/inspections.json` (banned symbols/namespaces/attributes, identifier/string/comment patterns) with id, severity, message; `validate` checks the file |
| `resharper_inspection_map` | Where ReSharper's inspection ids stand: the `rider` pack ids, the simplify_code/pack/tool mapping, the ids with no capability, plus the Inspection-type count of a local Rider install |
| `read_dot_settings` | Read the ReSharper `.DotSettings`/`.DotSettings.user` layers: inspection severities, disabled ids, custom dictionary words |
| `find_spelling_issues` | Words one edit away from a word the solution or `.visualMCP/spelling.dic` + the `.DotSettings` words already use |
| `naming_rules` | ✎ preview Apply `dotnet_naming_rule` from `.editorconfig`: list the violations with the expected name, or rename them (`apply=true`) |
| `background_analysis` | ✎ start/stop/refresh a warm loop that re-runs the inspection packs when the solution changes |
| `analysis_findings` | The cached findings of the last warm run, by severity, inspection or file — cheap enough to poll |
| `complexity_report` | McCabe cyclomatic, Sonar cognitive, Halstead and maintainability index — with the lines that own the score |
| `git_churn_hotspots` | Where a refactoring pays first: git history (how often a file changes) joined with Roslyn complexity (its worst method), scored as commits × worst cyclomatic; read-only, bounded window |
| `get_complexity_hotspots` | The most complex methods in the solution, ranked |
| `get_metrics` | Per-method complexity, lines of code, nesting depth and parameter count |
| `analyze_complexity` | Static Big-O estimate of time and space per callable, with loops, recursion shape, allocation sites and evidence |
| `find_dead_code` | Unreachable statements and always-false branches, from the control-flow graph |
| `find_unused_symbols` | Public/internal types and members with zero references, with the framework-invoked ones reported separately (`frameworkInvoked`) |
| `framework_entry_points` | The members a framework calls by convention — MVC actions, hub methods, hosted services, Blazor lifecycle, EF overrides, tests — with the reason |
| `find_reflection_usage` | Reflective lookups (`Activator`, `Type.GetType`/`GetMethod`, `Assembly.Load`, …) with the name each uses, and which names resolve to the solution |
| `list_generated_sources` | Source generators a project references and the syntax trees they emit, which live in the compilation but not as documents |
| `find_external_usages` | The used surface of assemblies outside the solution: types, members, call sites, per-assembly summary |
| `verify_change` | Run the suite on the working tree and on a git revision in a detached worktree, then diff the outcomes test by test: regressions, fixes, new/removed tests, verdict |
| `find_breaking_changes` | Removed or re-signed public/protected members between a git revision and the working tree |
| `get_quality_trend` | Did a branch make the code better or worse: two revisions (or `to=worktree`) measured with one syntactic rule set — average complexity per method, methods over 10/20, long methods, TODOs, size as context — with improved/regressed/unchanged and the files that moved most. Nothing is compiled, so a revision that no longer builds is still comparable |
| `find_duplicate_code` | Structural clone detection, normalised over names and literals |
| `analyze_test_quality` | Tests that cannot fail: no assertion, empty body, unreadable name, `async void` |
| `find_code_smells` | `async void`, empty catch, await-in-lock, long methods, deep nesting, large classes |
| `find_missing_validation` | Public methods taking a reference-type/string parameter with no null guard, plus DTO-shaped public classes/records whose properties carry no `System.ComponentModel.DataAnnotations` validation |
| `analyze_nullability` | Where nullable reference types actually protect: enabled context, `!` suppressions with lines, CS86xx warnings |
| `find_security_issues` | CWE-tagged scan: SQL/command injection, weak crypto, secrets, unsafe deserialization, SSRF, XXE, TLS bypass, path traversal |
| `find_async_anti_patterns` | `.Result`/`.Wait()`, `async void`, await in loops, `Task.Run` over sync code, missing `ConfigureAwait` |
| `find_performance_anti_patterns` | Allocation shapes read out of the code with the type model: `string +=` in a loop, a LINQ materialiser inside a loop, a collection or constant-size array allocated per iteration |
| `analyze_dependencies` | Project reference graph, cycles and root projects |
| `check_layer_violations` | Layer rules over real type usage: every dependency that points back up, with file and line |
| `export_dependency_graph` | The project, namespace or type dependency graph as Graphviz DOT or Mermaid (the type graph is cut at 300 nodes, most connected first) |
| `call_graph` | The call graph of a method as Mermaid/DOT: callers, callees or both, walked to a depth |
| `sequence_diagram` | The static call order of a method as a Mermaid sequence diagram |
| `control_flow_graph` | A method body's control-flow graph: Roslyn basic blocks, branch conditions, try/catch regions |
| `track_value` | The definitions (writes) and uses (reads) of the symbol on a line, with file:line, member and snippet |
| `localization_report` | Unused `.resx` keys, missing translations per culture, extra keys, and user-facing literals as candidates |
| `analyze_circular_namespace_dependencies` | Namespace-level cycles, computed from actual type usage |
| `analyze_dependency_injection` | Captive dependencies, duplicate registrations, unresolved services, constructor bloat, service locator |
| `analyze_namespace_coupling` | Afferent/efferent coupling, instability, abstractness, distance from the main sequence |
| `get_cohesion_metrics` | LCOM4: how many independent field clusters a class has, plus its stateless helpers |
| `plan_feature_change` | An ordered change plan for a type or member: what to understand, what breaks, files to touch, tests |
| `list_analysis_tools` | The analysis catalogue with when to use each tool |

## Refactoring

Each of these is semantic and **writes**: it resolves the symbol and rewrites every reference, rather
than doing a text replace. Most accept `dryRun` and default to a preview; the schema states the default.
See [Refactoring and style](guides/refactoring.md) for the workflow and guarantees.

| Tool | What it does |
|---|---|
| `apply_rename` | Rename a symbol everywhere (class, method, property, field, …) |
| `preview_rename` | Show what a rename would change, without writing |
| `rename_namespace` | Rename a namespace across declarations and references |
| `rename_tuple_component` | Rename one element of a named tuple, semantically |
| `move_type` | ✎ Move a type into its own file named after it |
| `move_type_to_outer_scope` | Hoist a nested type to its namespace, rewriting `Outer.Inner` |
| `move_type_to_nested_scope` | ✎ Move a namespace-level type into another type, rewriting every reference to `Target.Source` |
| `move_to_folder` | ✎ Move a file to the folder that matches its namespace (or an explicit folder) |
| `move_to_namespace` | ✎ Move a type to another namespace and update references |
| `move_static_members` | ✎ Move static methods/properties/fields to another type and re-point every use |
| `move_instance_method` | ✎ Turn an instance method into a static method of another type, receiver as first parameter |
| `extract_method` | ✎ Extract a contiguous statement block into a private method |
| `extract_method_candidates` | Find cohesive blocks in long methods that are worth extracting |
| `extract_class` | ✎ Move a self-contained member set into a new class, leaving forwarding members |
| `extract_interface` | ✎ Generate an interface from a class's public instance surface and add it to the base list |
| `extract_superclass` | ✎ Move self-contained members into a new base class |
| `inline_class` | ✎ Fold a helper class held in one field into its owner |
| `inline_symbol` | ✎ Inline a local, method or property at all use sites and remove the declaration |
| `inline_parameter` | ✎ Replace a parameter with the argument every caller passes, then remove it |
| `introduce_variable` | ✎ Extract an expression into a local variable |
| `introduce_field` | ✎ Hoist a context-free expression into a private readonly field |
| `introduce_parameter` | ✎ Turn a compile-time-fixed expression into a parameter, passing it at every call site |
| `introduce_part_of_string` | ✎ Lift a substring of a string literal into a private const |
| `change_signature` | ✎ Reorder, add or remove parameters and update every call site |
| `transform_parameters` | ✎ Wrap parameters in a parameter object, make them optional or required, retype them, or replace one with a property |
| `change_type` | ✎ Replace a declaration's type, only if the project still compiles |
| `change_nullability` | ✎ Toggle a nullable annotation, refused when it would add nullability diagnostics |
| `use_base_type` | ✎ Replace a declaration's type with a base type or interface, if it compiles |
| `add_params` | ✎ Add `params` to a method's last array parameter |
| `convert_to_primary_constructor` | ✎ Constructor parameters move into the type header; assignments become initializers |
| `convert_to_record` | ✎ Convert a class or struct to a record, optionally positional |
| `convert_type_kind` | ✎ preview Abstract class ↔ interface in one tool: the declaration and every derived type or implementer are rewritten together (`override` dropped or added), compiler-gated; instance state, structs and default interface methods are refused by name |
| `convert_anonymous_to_type` | ✎ Turn a `new { … }` into a real class in its own file |
| `constructor_to_factory` | ✎ Make the constructor private, add a static `Create`, rewrite `new T(...)` |
| `copy_type` | ✎ Duplicate a type under a new name, renaming its constructors |
| `encapsulate_field` | ✎ Turn a field into a property and rewrite every reference |
| `property_to_auto` | ✎ Trivial accessors become an auto-property; the backing field disappears |
| `property_to_function` | ✎ Property becomes `GetX()` / `SetX(value)`, uses rewritten |
| `function_to_property` | ✎ Parameterless non-void method becomes a property |
| `function_to_indexer` | ✎ Convert a method to an indexer (same-file call sites) |
| `indexer_to_function` | ✎ Convert an indexer to a method (same-file call sites) |
| `pull_members_up` | ✎ Move members into a base class, or add their signatures to an interface |
| `push_members_down` | ✎ Move a member into the only derived type |
| `implement_inpc` | ✎ Add `INotifyPropertyChanged` plumbing and convert properties to notifying ones |
| `generate_members` | ✎ Generate constructor, `Equals`/`GetHashCode` and `ToString` from fields/properties |
| `generate_builder` | ✎ Generate a fluent builder for a type: one `With<Property>` per public settable property and a `Build()`; compiler-gated |
| `generate_dto_from_json` | ✎ Generate a C# DTO from a JSON sample (types inferred from the data, init-only properties, nested objects as nested records); compiler-gated |
| `implement_members` | ✎ Generate stubs for the interface and abstract members a type still owes; refused unless the compiler errors disappear |
| `create_from_usage` | ✎ Create what a compiler error already assumes: the method or property on the receiver's type (CS1061), the static member on its type (CS0117), the overload whose signature is the call site (CS1501/CS7036), the type (CS0246) or a method on the enclosing type (CS0103) |
| `generate_docs` | ✎ Write the missing XML `<summary>`/`<param>`/`<typeparam>`/`<returns>` entries from the declaration name |
| `make_static` | ✎ Make an instance member static and rewrite call sites |
| `make_non_static` | ✎ Make a static member an instance member, routing each call through an instance already in scope |
| `generify` | ✎ Turn a concrete type used in a method or type into a type parameter, updating call sites and references |
| `static_to_extension` | ✎ Turn a static method into an extension method, rotating the receiver |
| `type_to_partial` | ✎ Mark every declaration of a type `partial` |
| `global_usings` | ✎ Move a project's plain usings into `GlobalUsings.cs` |
| `implicit_usings` | ✎ Toggle `<ImplicitUsings>`, writing the explicit usings when disabling |
| `convert_to_non_global_using` | ✎ Split one `global using` into per-file usings where needed |
| `optimize_usings` | ✎ Remove unused usings (optionally sort) |
| `reorder_members` | ✎ Order members by accessibility and kind |
| `remove_regions` | ✎ Strip `#region`/`#endregion` and keep the code |
| `safe_delete` | ✎ Delete a symbol only when Roslyn finds zero references |
| `invert_if` | ✎ Swap an if/else's branches and negate the condition |
| `context_action` | ✎ preview The ReSharper context actions that are neither style rewrites nor refactorings: import a type's using, argument guards (null, all parameters, string not null or empty, collection not empty, dictionary key, enum defined, index range, variable), surround-with (try/catch, try/finally, if, using, #region), braces, loop conversions (`foreach`↔`for`, invert), `if`→`switch`, concatenation↔interpolation, digit separators (add and remove), remove an argument name, merge or split an `if` chain, join a null check with its use, `use_var` / `use_explicit_type`, split a declaration, remove braces, `#nullable enable` migration, `ConfigureAwait(false)`, and string literals between regular, verbatim and raw — 36 action ids, compiler-gated |
| `templates` | ✎ preview ReSharper's templates as data: `list` the 18 live, 10 postfix (`.if`, `.null`, `.var`, `.await`, `.foreach`, …) and 8 file templates plus the team's own in `.visualMCP/templates.json`; `expand` a live shortcut or a postfix expression at a line; `create` a new file (class, interface, record, struct, enum, static class, abstract class, xUnit test); `save`/`delete` the custom ones — compiler-gated |
| `fix_complexity` | ✎ preview Make the codebase simpler in one call: rank every method, lower the worst with the same moves as `reduce_complexity`, report before/after — whole solution, project or file |
| `reduce_complexity` | ✎ Lower cyclomatic and cognitive complexity by extracting blocks, naming booleans and adding guard clauses, re-measuring until a target |
| `remove_params` | ✎ preview Remove the `params` modifier from a method's last array parameter; refused when a call site passes the elements individually |
| `inline_field` | ✎ preview Inline a field at every use and remove it; refused when it is written outside its declaration or used from another file |
| `extension_to_static` | ✎ preview Turn an extension method into a static one and rewrite every `x.M(a)` to `Type.M(x, a)` |
| `extract_members_to_partial` | ✎ preview Move members into a new `<Type>.<Suffix>.cs` partial file; both files are gated together |
| `invert_boolean` | ✎ preview Negate every return (or the expression body) of a bool member, and rename it across the solution with `newName` |
| `safe_delete_file` | ✎ preview Delete a source file only when nothing outside it uses its declarations; deletion is destructive, so it previews by default |
| `safe_delete_folder` | ✎ preview The same for a folder of C# files; refused when the folder holds anything else |

## Code style

| Tool | What it does |
|---|---|
| `simplify_code` | ✎ preview Apply the IDE's mechanical style suggestions to C#: 87 rule ids, at document, project or solution scope, compiler-gated, preview by default; IDE0005/IDE0032/IDE0290 are delegated to their own tools |
| `simplify_vb_code` | ✎ preview The same engine for Visual Basic: 15 rule ids, compiler-gated, preview by default |
| `code_cleanup` | ✎ preview Run the cleanup steps in order (style → usings → regions → reorder → format) over a document, project or solution; `steps` or a named `profile` — the built-in `full`/`reformat`/`style`/`usings`/`regions`/`reorder`/`safe` or a team one from `.visualMCP/cleanup-profiles.json`, which `saveProfileAs` writes |
| `layout_code` | ✎ preview CodeMaid's Format Document rules Roslyn's formatter does not implement: reflow full-line `//` comments to a width, join a line range (refused when the token stream would change), collapse runs of blank lines and trim them at the braces |
| `format_code` | ✎ preview Reformat with Roslyn using the project's `.editorconfig`, preserving line endings; document, project or solution |

## Build, run and test

| Tool | What it does |
|---|---|
| `build_project` | Run `dotnet build` and return structured errors/warnings; runs the configured analyzers by default and can skip copying output so a running app does not lock files |
| `analyze_binlog` | ✎ Read an MSBuild binary log — or run `dotnet build -bl` and analyse it — returning errors/warnings with project, file and line, the duration of every project and target, the target that dominates the wall clock, and the project graph |
| `project_properties` | ✎ preview Read the evaluated MSBuild view (`dotnet msbuild -getProperty`/`-getItem`, so what `Directory.Build.props` and conditions contribute), and set/unset one property in the project or the nearest `Directory.Build.props`, build-gated |
| `run_project` | Launch `dotnet run` with a timeout and capture startup output and the listening URL |
| `run_config` | ✎ preview Save and list the ways a project is launched — project, application arguments, configuration, environment variables — in `.visualMCP/run-configs.json`; `run_project configName=…` applies one |
| `launch_settings` | ✎ preview `Properties/launchSettings.json` as data: list, add/update or remove profiles, preserving `$schema`, `iisSettings` and the profiles nobody touched |
| `run_command` | Execute a shell command in PowerShell, pwsh, cmd or bash (PowerShell by default on Windows, bash elsewhere) — pipes, redirection, nested shells, long scripts; the process tree is killed at the timeout |
| `run_tests` | Run `dotnet test`, parse TRX, return pass/fail/skip counts, per-project results and failure details |
| `get_test_coverage_map` | Per-class and per-method line coverage, plus branch coverage (Cobertura `branch-rate`); requires `coverlet.collector` in the test projects |
| `run_test` | Run one test or one class without hand-building `--filter`; structured TRX result (passed/failed, failure messages) and the failures are recorded |
| `rerun_failed` | ✎ Re-run exactly the tests that failed in the last run, from `.visualMCP/last-test-run.json` |
| `tests_covering_symbol` | Which test methods reference this symbol (semantic, `[Fact]`/`[Theory]`/`[Test]`/…), with file:line |
| `generate_tests` | ✎ preview Scaffold an xUnit test class with one `[Fact]` per public method; compiler-gated, written into the test project |
| `test_impact_analysis` | Which tests a change can affect: git diff → the symbols the changed files declare → the test methods that reference them, with a ready filter |
| `run_affected_tests` | The same analysis, then runs exactly those tests with the composed filter (or answers `no-affected-tests`) |
| `list_tests` | The tests the source declares: class, method, framework, parameterized, inline data rows; dynamic sources named |
| `debug_test` | ✎ Debug one test: VSTest host under the DAP debugger (`VSTEST_HOST_DEBUG`), breakpoints set before resuming; `sessionId` reads the host output |
| `continuous_testing` | ✎ start/stop/status/run-now: run the tests after a source save (debounced) and keep the last result |
| `find_flaky_tests` | Run the selection several times and report the tests whose outcome changed, apart from those that never passed |
| `run_mutation_tests` | ✎ Run Stryker.NET over a test project: mutation score, killed/survived/timed-out/no-coverage counts, the worst files and the surviving mutants with file and line |
| `dotnet_publish` | Publish a project (RID, self-contained, single-file, output folder) with a structured result |
| `publish_profile` | ✎ preview `Properties/PublishProfiles/*.pubxml` as data: list, create/update or delete a profile by the MSBuild properties `dotnet publish -p:PublishProfile` reads (`PublishDir`, `RuntimeIdentifier`, `SelfContained`, `PublishSingleFile`, …) |
| `add_project_reference` | ✎ Add a `<ProjectReference>` (`dotnet add reference`) and reload the workspace |
| `remove_project_reference` | ✎ Remove a `<ProjectReference>` |
| `nuget_pack` | ✎ Pack a project into a .nupkg, with an optional version and output folder |
| `nuget_push` | ✎ Push a .nupkg to a feed; the API key is used but never echoed back |
| `extract_build_log_errors` | Pull compiler/linker/MSBuild errors out of a log file, including UTF-16 logs |
| `get_environment_info` | OS, installed .NET SDKs and runtimes, Visual Studio, `cl.exe`, and the existence/size of given paths |
| `stop_server` | Stop other VisualMCP instances to release locks on the built executable |

## Debugging

A real Debug Adapter Protocol debugger over [netcoredbg](https://github.com/Samsung/netcoredbg)
(installed on demand into the shared tools directory — `debug_install`). Sessions live across calls,
so a stop that happened between two tool calls is still there. `debug_status`, `debug_threads`,
`debug_stack`, `debug_variables`, `debug_evaluate`, `debug_output` and `debug_sessions` are read-only;
the others change the session or the debuggee.

| Tool | What it does |
|---|---|
| `debug_status` | Whether the adapter is installed, where, and how to point at another build (`VISUALMCP_NETCOREDBG`) |
| `debug_install` | ✎ Download and extract netcoredbg from the Samsung release (idempotent; `force` reinstalls) |
| `debug_launch` | ✎ Start a built `.dll`/`.exe` (or a `.csproj`: the newest `bin/` assembly) under the debugger; breakpoints can be set in the same call, and the call waits for the first stop |
| `debug_attach` | ✎ Attach to a running process id |
| `debug_breakpoints` | ✎ Replace one file's breakpoints (`line`, `condition`, `hitCondition`); reports verified vs unverified |
| `debug_continue` | ✎ Continue and wait for the next stop or exit |
| `debug_step` | ✎ Step over / into / out |
| `debug_pause` | ✎ Break into the running process |
| `debug_exceptions` | ✎ Break on exceptions (`all`, `user-unhandled`) |
| `debug_threads` | The threads, with the stopped one marked |
| `debug_stack` | The call stack with file:line per frame |
| `debug_variables` | Locals / Arguments / Autos of a frame, with optional member expansion |
| `debug_evaluate` | Evaluate a watch expression in the stopped frame |
| `debug_output` | The debuggee's console output, plus the adapter's stderr |
| `debug_terminate` | ✎ End the session and terminate the debuggee |
| `debug_sessions` | Every live session with its state, stop location and breakpoint files |

## Benchmarks

| Tool | What it does |
|---|---|
| `run_benchmarks` | Build and run an existing BenchmarkDotNet project, return the summary table |
| `quick_benchmark` | Benchmark an ad-hoc code snippet without writing a `[Benchmark]` class; optional project reference |

## Profiling

All native: none of these spawns dotnet-trace, dotnet-counters, dotnet-gcdump, dotnet-dump or
dotnet-stack. Full workflows in [Profiling](guides/profiling.md).

| Tool | What it does |
|---|---|
| `profile_processes` | List .NET processes that expose a diagnostics endpoint |
| `profile_tools` | Status, install and update the seven external diagnostics tools |
| `profile_collect` | Collect a real `.nettrace` through EventPipe: cpu/gc/exceptions/threading/runtime/diagnose/async/database/instrumentation/custom, bounded or start/stop |
| `profile_analyze` | CPU hotspots and stacks, allocation sites, GC pauses, exceptions with throw sites, contention, thread pool, JIT, timeline, findings, report |
| `profile_root_cause` | Why the measured code costs what it costs, joined with the solution's metacode AST: the loop chain and nesting, a LINQ materialiser per iteration, an allocation inside a loop, a string concatenation per element, an await/lock in a loop, `Task.Result`/`.Wait()` |
| `profile_async` | The .NET async story: exact await durations (TplEventSource pairs), tasks that threw, named async operations, and state machines that never completed |
| `profile_database` | The database story: commands with text and duration, the shapes that repeat (the N+1 signal), connection opens, the share of the window spent in data access |
| `profile_events` | The Events Viewer: every provider/event pair of a trace with counts, payload field names and one sample event — works on custom providers |
| `profile_instrumentation` | Exact call counts and inclusive/exclusive times, no sampling: rewrite an assembly's IL with an injected probe (`action=instrument`), run the copy, read the enter/exit pairs (`action=analyze`) |
| `profile_counters` | EventCounter time series (System.Runtime, ASP.NET Core, Kestrel, HTTP, sockets, gRPC) |
| `profile_stacks` | Every managed thread's stack, plus contended monitors and the holding thread |
| `profile_heap` | Managed heap by type, LOH and pinned bytes, finalizable objects, largest objects, duplicate strings |
| `profile_retention` | Why a type is still alive: shortest GC-root chains (each frame names the holding field) and the referrer types |
| `profile_dump` | Collect a dump (normal/withheap/triage/full) or analyse an existing `.dmp` with ClrMD |
| `profile_symbols` | Inspect PE debug records and download PDBs so frames resolve to `file:line` |
| `profile_monitor` | Start, probe and stop a loopback dotnet-monitor |
| `profile_allocations` | Attribute sampled allocation bytes to the allocating method, type and `file:line`, with full stacks |
| `profile_calltree` | Inclusive/exclusive CPU call tree, with optional folded-stacks export |
| `profile_hotpaths` | Ranked hot methods, root-to-leaf paths and branch points; CPU, allocations, exceptions or mixed |
| `profile_hotlines` | Which source line inside a method burns the CPU, resolved from the sampled instruction pointer through IL and the PDB |
| `profile_io` | Where threads block in I/O — file, HTTP, network, database, pipe, console, sync-over-async, lock, sleep — with call site and duration |
| `profile_compare` | Diff two traces or two heap snapshots, with regression findings |
| `profile_diagnose` | Counters + trace + stacks + heap in one call, with correlated findings and one report |
| `profile_slowdown` | One verdict: cpu, gc, io-wait, sync-over-async, lock-wait or blocked-native, ranked by the share of the window it explains |
| `profile_memory_growth` | One verdict: growth, shrink, stable or single-snapshot, with the type and allocation site |
| `profile_threadpool` | One verdict: starved, at-risk, burst, healthy or inconclusive, with the blocking sites |
| `profile_review_change` | One verdict: regression, improvement, unchanged or inconclusive, on per-second normalised rates |

## NuGet and packages

| Tool | What it does |
|---|---|
| `list_nuget_packages` | Packages with requested/resolved versions per target framework, and version conflicts across projects |
| `check_nuget_updates` | Outdated packages: current and latest version, per project |
| `check_nuget_vulnerabilities` | Known advisories (GHSA), with severity and links, including transitive packages |
| `fix_nuget_vulnerabilities` | ✎ Update vulnerable top-level packages to their latest stable version |
| `locate_nuget_package` | Find a package's DLLs in the local NuGet cache, grouped by target framework |
| `inspect_package` | One-shot: locate a package and list its public API, without running it |
| `inspect_packages` | The same for several packages (`id` or `id@version`) in one call |
| `decompile_package_type` | One-shot: locate a package and decompile one type to C# |
| `search_nuget_packages` | Search the nuget.org gallery by id or keywords: version, description, downloads, authors, tags |
| `nuget_sources` | ✎ List, add, update, remove, enable or disable the package sources through `dotnet nuget`; credentials are passed to NuGet and never echoed back |
| `add_nuget_package` | ✎ Add a package reference and restore |
| `remove_nuget_package` | ✎ Remove a package reference |
| `update_nuget_package` | ✎ Update a package to a newer version or the latest stable |
| `unify_nuget_version` | ✎ Set one version of a package across every project that references it |

## Modernization

| Tool | What it does |
|---|---|
| `aspire` | ✎ Status, install and scaffold .NET Aspire: AppHost + ServiceDefaults, solution entries, project references, `AddProject<T>`/`AddServiceDefaults` wiring, then a build that reverts bad wiring |
| `update_framework` | ✎ preview Move projects to a newer target framework, keeping platform suffixes and `global.json` in sync; a failed build restores every edited file |
| `update_solution` | ✎ preview The framework bump plus every outdated top-level NuGet package in one gated pass, Central Package Management aware, full rollback on failure |

## EF Core

| Tool | What it does |
|---|---|
| `ef_migrations` | ✎ Run the project's own `dotnet ef`: `list`, `add`, `script`, `remove`, `update`, `info`, `dbcontext-list`, with the exact command, exit code, stdout and stderr as structured data |

## Razor and Blazor

| Tool | What it does |
|---|---|
| `razor_pages` | Inventory of `.cshtml`/`.razor` files: kind, `@page` route, `@model`, layout, sections, `@inject`, handlers, code-behind, markup counts |
| `analyze_razor_pages` | Markup, accessibility and maintainability findings (`RZ2xxx`) with `file:line` and a fix |
| `find_razor_vulnerabilities` | CWE-tagged security findings (`RZ1xxx`): XSS, CSRF, secrets, unsafe URLs, CSP |
| `razor_edit` | ✎ preview Set/remove an attribute, add a directive, or insert/replace/delete lines; `expect` guards the current text, the page is re-scanned before writing, and the write is revertible |

## XAML

The markup half of a WPF / WinUI 3 / MAUI / Avalonia solution: the files are read from disk, the
code-behind is resolved through Roslyn. It does not compile the XAML, so bindings and styles are not
proved — the notes say so.

| Tool | What it does |
|---|---|
| `xaml_pages` | Inventory of `.xaml`/`.axaml` files: kind (window/page/userControl/maui/resourceDictionary/application), `x:Class` resolved to its code-behind type, markup counts |
| `analyze_xaml` | Findings `XAML0001`–`XAML0009`: duplicate `x:Name`/`x:Key`, missing or unresolvable `x:Class`, an event handler with no method, a resource key nothing defines, a `DataTemplate` without its type, a name colliding with a member, invalid XML |
| `xaml_navigate` | Follow one name to everything attached to it: `name` (declarations + `ElementName` uses + the generated field), `resource` (definitions + uses), `style` (BasedOn chain and derived styles), `handler` (markup + method) |

## SQL

`.sql` files as a scanned language: comments and string literals are masked first, statements are split
and the rules that need no schema are applied. It is a pattern scanner, not a dialect parser — it never
claims that a database accepts a statement.

| Tool | What it does |
|---|---|
| `sql_files` | Inventory of the solution's `.sql` files: lines, statements, verbs, tables named, finding count per file; `bin`, `obj`, `node_modules` and VCS folders are skipped |
| `analyze_sql` | Findings `SQL0001`–`SQL0010` with `file:line:column`, the source line and a fix: unterminated literal/comment/bracket, unbalanced parentheses, `SELECT *`, `UPDATE`/`DELETE` without `WHERE`, `DROP` without `IF EXISTS`, dynamic SQL by concatenation, an unclosed transaction, a leading-wildcard `LIKE`, `ADD … NOT NULL` without `DEFAULT`, an `INSERT` column/value count mismatch |

## Decompilation and reflection

| Tool | What it does |
|---|---|
| `inspect_assembly` | A compiled `.dll`'s public API: types, base types, interfaces, members and signatures; metadata only |
| `inspect_assemblies` | The same for several assemblies in one call |
| `find_assembly_api` | Regex search over type and member names; matching types come back with their members, and a member match names its type |
| `decompile_type` | Decompile one type from an assembly to C# with the ILSpy engine |
| `decompile_types` | The same for several types of one assembly in one call |
| `assembly_workspace` | Decompile an assembly INTO a Roslyn workspace (one document per type, cached per MVID) and navigate it: `load`, `list`, `search` (CamelHumps too), `source`, `references`, `outline`, `unload` |
| `decompile_assembly` | Decompile a whole module, or a filtered/paged set of types; can write one file per type |
| `disassemble` | The IL view of a module: each method body decoded into instructions with the metadata tokens resolved (`call System.String::Concat`, the `ldstr` literals), filtered by type and method |

## Metacode

| Tool | What it does |
|---|---|
| `metacode_translate` | Lower C#, Visual Basic, a Razor page, a Ghidra snapshot or canonical pseudocode into one pseudocode language, with `RAW` constructs and diagnostics listed |
| `metacode_compare` | Translate two sources and diff the canonical text line by line — the "same algorithm?" check |
| `metacode_index` | The solution's metacode persisted in `.visualMCP/visualmcp.db`: status, build, model-level cyclomatic/cognitive complexity, stored trees, and the complete AST (`ast`: every node with parent link, property slot, position, depth, span and payload) |
| `metacode_generate` | Generate C# from pseudocode, a stored tree or a file; what C# cannot express is lowered with a diagnostic |
| `translate_python_file` | Translate a `.py` file into canonical pseudocode (runs CPython's `ast` dumper, or accepts a supplied dump) |
| `translate_cpp_file` | Translate a C/C++ file into canonical pseudocode (runs `clang -ast-dump=json`, or accepts a supplied dump) |
| `decompile_to_metacode` | Decompile a .NET assembly with ILSpy and print it as canonical pseudocode instead of C# |
| `analyze_native_binary` | Run Ghidra headless on a native binary and print recovered functions as pseudocode with prototype and entry address; the run is also persisted in `native_artifact` / `native_function` / `native_call` (`save=false` to skip) |
| `metacoding` | ✎ preview Write through the model: `decompile` (a managed assembly becomes metacode trees in the solution database, a native binary is routed to Ghidra and stored in the native tables), `rewrite` (replace a declaration from pseudocode), `insert` (add a generated member). Compiler-gated; mechanical style fixes stay in `simplify_code` |

## Git

The five inspect tools — `git_status`, `git_diff`, `git_log`, `git_show`, `git_blame` — are read-only.
Everything else changes the repository; the destructive cases require `confirm=true`.

| Tool | What it does |
|---|---|
| `git_status` | Branch, upstream ahead/behind, staged/modified/untracked/conflicted files |
| `git_diff` | Working, staged or a ref/range diff; optionally a per-file stat |
| `git_log` | Recent commits with hash, author, date, subject; filter by path |
| `git_stage` | Stage paths or everything |
| `git_commit` | Commit staged changes with a message; optionally stage all first |
| `git_create_branch` | Create a branch and optionally switch to it |
| `git_branch` | List, create, delete, rename or switch branches |
| `git_squash` | Fold the last N commits into one (refused on a dirty tree) |
| `git_amend` | Reword the last commit, keeping its tree |
| `git_merge` | Merge with default/no-ff/ff-only/squash, or abort; reports conflicts |
| `git_conflicts` | The conflict model: `list` the conflicted files with their hunks (ours/theirs/base labels, line counts, previews), `show` one file's three sides and diff3 text, `resolve` with `ours`/`theirs`/`both`/`base`/`custom` for one hunk (`blockIndex`) or the whole file, staging it once no marker remains |
| `git_rebase` | Start, continue, abort or skip a rebase; `action=plan` is the interactive rebase as data — a JSON todo list of `pick`/`reword`/`squash`/`fixup`/`drop` steps with messages, applied through `GIT_SEQUENCE_EDITOR`, no editor opened |
| `git_cherry_pick` | Copy commits onto the current branch, or continue/abort/skip |
| `git_revert` | Undo commits with new commits |
| `git_reset` | soft/mixed/hard; hard requires `confirm=true` |
| `git_stash` | push, list, pop, apply, drop, show, clear |
| `git_patch` | Named shelves on disk: `create` (working, staged or a revision/range), `list`, `show`, `apply` (with `--check`, `--3way` or `reverse=true`), `delete` (with `confirm=true`) |
| `git_tag` | List, create (annotated when messaged) or delete tags |
| `git_remote` | List, add, remove or change remotes |
| `git_show` | One commit in full, plus one file's diff |
| `git_blame` | Who last touched each line, with commit and date |
| `git_sync` | fetch, pull, push; force uses `--force-with-lease` and requires `confirm=true` |
| `git_worktree` | List, add (on a branch, optionally new), remove or prune linked working trees |
| `git_bisect` | Start, mark good/bad, skip, log, view or reset a binary search; reports when it finds the first bad commit |
| `git_submodule` | status (structured), update, init, sync or deinit |
| `git_host` | GitLab, Azure DevOps and Bitbucket Cloud: `detect` the provider/host/project/repository from the remote, `list-prs`, `view-pr`, `create-pr`, `comment-pr`, `merge-pr`, `list-issues`, `create-issue`, `pipelines` (`confirm=true` for the writes; credentials from `GITLAB_TOKEN`, `AZURE_DEVOPS_EXT_PAT`, `BITBUCKET_*`) |
| `git_run` | Escape hatch: any git command, verbatim, with exit code and output |

## GitHub

Requires the authenticated [`gh` CLI](https://cli.github.com/). `list`, `view` and `diff` are read-only;
the other actions change GitHub, and merges/deletes require `confirm=true`.

| Tool | What it does |
|---|---|
| `gh_repo` | list, view, create, fork, clone, edit, delete |
| `gh_pr` | list, view, create, merge, close, reopen, comment, `review` (a review with line-anchored comments: `comments` as `[{path,line,side,body}]`, event comment/approve/request-changes), `review-comments`, `checks`, diff, checkout, ready |
| `gh_issue` | list, view, create, close, reopen, comment |
| `gh_release` | list, view, create, upload assets, delete |
| `gh_actions` | Runs list/view/logs, rerun, cancel, workflows, dispatch |
| `gh_secret` | list, set, delete repository Actions secrets |
| `gh_branch_protection` | Read-only inspection of branch protection: `get` (protected, required checks/reviews, enforce-admins, restrictions), `checks` (strict mode and the contexts), `rules` (repository rulesets that apply) |
| `github` | Escape hatch: any `gh` command, verbatim |

## Collaboration

| Tool | What it does |
|---|---|
| `code_review` | The review brief for a diff: changed files by kind (source/test/ui/docs/config), additions/deletions, a risk checklist (deleted test, source without test, 400+ line files, new TODO/FIXME markers with their line), the worst methods of each changed C# file, and — with `includeInspections=true` — the inspection findings inside the changed files |

## Docker

`docker_status`, `docker_ps`, `docker_inspect`, `docker_logs`, `docker_images`, `docker_networks`,
`docker_volumes` and `docker_stats` are read-only; the rest change the engine.

| Tool | What it does |
|---|---|
| `docker_status` | Engine availability, versions and containers |
| `docker_ps` | Containers with id, name, image, state, ports; running or all |
| `docker_inspect` | A container's full configuration as JSON |
| `docker_logs` | A container's logs, with tail, timestamps and since |
| `docker_control` | start, stop, restart, pause, unpause, kill |
| `docker_rm` | Remove a container (force, volumes) |
| `docker_run` | Create and start a container with name, ports, env, volumes, network, command |
| `docker_images` | List images, or inspect one |
| `docker_pull` | Pull an image |
| `docker_image_rm` | Delete an image |
| `docker_networks` | List networks |
| `docker_volumes` | List volumes |
| `docker_stats` | One-shot CPU/memory/network/block I/O sample |
| `docker_compose` | up, down, ps, logs, restart, stop, start, pull, build |
| `docker_prune` | Reclaim disk; requires `confirm=true` |

## Kubernetes and Helm

Through the ecosystem's own clients: `kubectl` for the cluster, `helm` for releases. `k8s_status`,
`k8s_context`, `k8s_get`, `k8s_describe` and `k8s_logs` are read-only; `k8s_apply`, `k8s_delete` and
`k8s_rollout` change the cluster and require `confirm=true` (apply defaults to a server-side dry run).
A missing client or an unreachable cluster is a structured answer, not an exception.

| Tool | What it does |
|---|---|
| `k8s_status` | Whether kubectl is installed, client and server versions, the current context, whether a cluster answers, and the helm version when present |
| `k8s_context` | `current`, `list` (the contexts table) or `use` to switch context |
| `k8s_get` | Resources as rows (name, namespace, phase or `N/M ready`, restarts, age) parsed from `get -o json`; `output=yaml\|json\|wide\|name\|jsonpath=…` for the client's own view; kinds may be comma-separated |
| `k8s_describe` | The full `kubectl describe` text: events, conditions, containers, scheduler decisions |
| `k8s_logs` | A pod's logs with `tail`, `since`, `container` and `previous` |
| `k8s_apply` | ✎ Apply a manifest from a file or from `content`; server-side dry run unless `dryRun=false` with `confirm=true` |
| `k8s_delete` | ✎ Delete one resource; requires `confirm=true`, `ignoreNotFound` for idempotence |
| `k8s_rollout` | `status`, `restart` or `undo` (with `toRevision`) of a workload; restart/undo require `confirm=true` |
| `k8s_helm` | `list`, `status`, `history`, `get-values`, `template` (offline render with values file / `--set`) and `repo-list` |

## Node and JavaScript/TypeScript

The toolchain a .NET solution often carries next to it, run through the project's own package manager
(npm, pnpm or yarn — detected from `packageManager`, then the lockfile) and the project's own installed
`tsc`/`eslint`. Nothing is downloaded implicitly: a missing compiler is reported with the command that
installs it.

| Tool | What it does |
|---|---|
| `node_info` | The nearest `package.json`, its name/version, the package manager and its version, the scripts, dependency counts, whether TypeScript and ESLint are installed |
| `node_install` | Install the lockfile (`ci` / `--frozen-lockfile`), add packages (`dev=true` for `--save-dev`) or remove them |
| `node_script` | Run one `package.json` script with arguments forwarded after `--`; refuses a script that does not exist and lists the ones that do |
| `node_typecheck` | `tsc --noEmit --pretty false` parsed into `TSxxxx` diagnostics with file:line:column and counts — the JS/TS equivalent of `get_diagnostics` |
| `node_lint` | `eslint --format json` parsed into findings (file, rule, severity, position), counts and the rules that fire most; `fix=true` runs `--fix` |
| `node_outdated` | `outdated --json` normalised across npm, pnpm and yarn 1 |
| `node_audit` | `audit --json` normalised: severity counts and per package the title, vulnerable range and fix |

## Planning

`board_list`, `board_show` and `board_board` are read-only; the other board tools change the store.

| Tool | What it does |
|---|---|
| `board_add` | Create a kanban card with column, priority, labels and assignee |
| `board_update` | Edit a card's fields; every change goes into its history |
| `board_move` | Move a card across backlog → todo → in_progress → review → done |
| `board_comment` | Append a comment to a card |
| `board_list` | Filter cards by column, priority, assignee, label or text |
| `board_show` | One card with all comments and its full history |
| `board_board` | The whole board as five columns, with counts and the done share |
| `board_import` | ✎ Create many cards in one call from a written backlog, keyed on the title (`skip`/`update`/`duplicate`) |
| `board_export` | The board as Markdown, one section per column |
| `board_delete` | Delete a card with its comments and history |
| `create_task_checklist` | Write a Markdown `Task \| Done` checklist for a multi-step task |
| `update_task_checklist` | Tick steps off (or undo) in that checklist |

## Automation and protocols

| Tool | What it does |
|---|---|
| `run_plan` | Execute a dependency graph of tool calls and shell commands in one round trip, with validation, parallelism, path-based conflict avoidance and per-step timeouts |
| `http_invoke` | Call any HTTP endpoint with method, headers and body; accepts self-signed localhost certificates |
| `run_http_file` | Execute a REST-client `.http`/`.rest` file (variables, `###` separators, headers, bodies) and report each response |
| `signal_r_connect` | Connect to a SignalR hub and return a session id |
| `signal_r_subscribe` | Buffer messages for one hub method |
| `signal_r_events` | Drain buffered events, optionally waiting |
| `signal_r_invoke` | Call a hub method, with a result or fire-and-forget |
| `signal_r_logs` | Connection lifecycle entries for a session |
| `signal_r_list_sessions` | Active SignalR sessions and their states |
| `signal_r_disconnect` | Close a session and free its resources |

## Maintenance

| Tool | What it does |
|---|---|
| `command_log` | Read the tool-call log from the solution store (date, tool, arguments, redacted secrets) |
| `edit_history` | Read every file write this server made, with tool, rules and hashes; says whether it is revertible |
| `revert_edit` | ✎ Restore a file to the state before one recorded edit, refusing if it changed since |
| `local_history` | ReSharper's Local History in the solution store: `snapshot` captures each changed source file (gzipped, deduplicated on its hash, newest 50 per file), `history` lists them, `diff` compares one with the current file or another snapshot, `restore` ✎ preview writes one back and snapshots the text it replaces |
| `package_facts` | The resolved NuGet graph from `obj/project.assets.json` — refresh, list, dependencies |
| `reference_index` | The persisted symbol index: build, status, find, describe, refs, callers, callees, hierarchy, outline, metacode, unused |
| `host_system` | OS family, architecture, newline convention, case sensitivity, and the line-ending style resolved for a path |
| `fix_line_endings` | ✎ preview Normalise line endings at file, directory, project or solution scope (`auto`, `lf`, `crlf`) |

## Scaffolding

| Tool | What it does |
|---|---|
| `list_licenses` | The licence catalogue: id, name, SPDX identifier, family (permissive, weak/strong/network copyleft, public-domain, source-available), OSI approval, patent grant |
| `add_license` | ✎ Write a LICENSE from a bundled template — 20 licenses, MIT to AGPL and PolyForm (`list_licenses` for the catalogue) |
| `add_contributing` | ✎ Write CONTRIBUTING.md, optionally with a CLA that allows relicensing |
| `add_ci_workflow` | ✎ Write a GitHub Actions workflow: multi-OS build, or self-contained release binaries on a tag |

## Self-management

| Tool | What it does |
|---|---|
| `check_for_update` | Compare the installed version with the latest GitHub release |
| `self_update` | ✎ Download and stage the latest release, then restart the server |

---

Not finding a tool by name? Open `http://127.0.0.1:4677/tools` while the server runs: it lists the live
catalogue with every argument, type, default and description.

Next: [Reading and analysing code](guides/code.md) · [Refactoring and style](guides/refactoring.md)
