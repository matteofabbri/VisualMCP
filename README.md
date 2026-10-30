# VisualMCP

An MCP (Model Context Protocol) server that gives an AI coding client — Claude Code, ZCode, or any MCP-capable tool — IDE-grade access to a .NET solution: semantic navigation, analysis, refactoring, debugging, profiling, and **309 tools in one local process**, built on Roslyn's `MSBuildWorkspace` and the real project graph.

> **Public documentation:** the user-facing guide starts at [docs/public/README.md](docs/public/README.md) — install, concepts, the complete tool catalogue, guides and limits.

## How it works — local by design

VisualMCP is a local executable. Your MCP client starts it as a child process and speaks Model Context Protocol over stdin/stdout. The server opens the solution from disk with Roslyn's `MSBuildWorkspace`, keeps derived data in `<solution>/.visualMCP/visualmcp.db`, and optionally serves a read-only dashboard on `http://127.0.0.1:4677`. There is no hosted service, no account, and no inbound port apart from that loopback dashboard. It is the same model as a Language Server Protocol server: a local process reads files and replies to the client that started it.

The server does not upload source code, telemetry or usage data. It opens the network only when a specific tool requires it: NuGet restore during a build, package or template upgrades, `download_file`, the GitHub CLI, PDB downloads, the update check, or the one-time Ghidra download when `analyze_native_binary` finds no local installation.

What does leave your machine is the **conversation** your client sends to its model provider. That includes tool results — so if the model calls `read_file` or `read_symbol`, the source it receives is part of that conversation. This is true of any MCP tool and is controlled by your client, not by VisualMCP. The full privacy model is in [How it works → Privacy](docs/public/how-it-works.md#privacy).

> **Running it remotely would break it** — tools resolve paths like `C:\REPOSITORY\...` on the local filesystem. If the process ran on a different machine those paths would not exist. The stdio transport is intentionally local-only.

## Features

A selection; the complete catalogue, with arguments and defaults, is [docs/public/tools.md](docs/public/tools.md).

| Tool | Description |
|------|-------------|
| `list_solutions` | Scan a directory tree for `.sln` / `.slnx` files |
| `load_solution` | Open a solution with Roslyn MSBuildWorkspace (loads the full semantic model) |
| `solution_map` | The codebase map in one call: projects and what each is, the biggest namespaces and types, entry points, repository Markdown |
| `capability_report` | What the running server can do: tools by area with live counts, rule catalogues, language layers |
| `get_project_info` | Documents, project/assembly references, and NuGet packages for a project |
| `read_file` | Read a source file with line numbers and optional range |
| `read_files` | Read **several** files in one call (path list, budgeted output) — replaces a loop of `read_file` |
| `read_tree` | Directory tree to a chosen depth in one call (prunes `bin`/`obj`/`.git`) — replaces a walk of `list_directory` |
| **Search and indexes** | |
| `search_text` | Regex search over any files, with `file:line` and optional context |
| `search_code` | The same regex, but every hit comes back as the whole declaration that contains it — search and read in one call |
| `text_index` | Full-text index in the solution store: one row per line, camel-split words, BM25 ranking, incremental build |
| **Navigation** | |
| `find_symbol` | Semantic search for any named symbol across the loaded solution |
| `find_references` | Every location where a symbol is used (not just declared) |
| `find_implementations` | All types that implement an interface, with per-member mapping |
| `find_derived_types` | Downward inheritance tree for a class or interface |
| `find_callers` | Call hierarchy — every method that calls a given method |
| `get_symbol_info` | Resolve the symbol at a specific file + line (like hovering in VS) |
| `get_type_members` | All members of a type with full signatures and XML docs |
| `read_symbol` | The **source** of a declaration (type/method body) — go-to-definition that shows the code |
| **Analysis** | |
| `solution_audit` | Whole-solution health report in one call: inspections by id, compiler errors/warnings, complexity hotspots, largest files, project graph + cycles, suggested first moves |
| `get_diagnostics` | Compiler errors and warnings (equivalent to VS Error List) |
| `inspect_code` | Built-in Rider-style inspection packs (Roslynator, .NET analyzers, AsyncFixer, Meziantou, StyleCop) plus the house rules |
| `apply_code_fix` | Apply a finding's own `CodeFixProvider`, optionally FixAll at document/project/solution scope; dry run by default |
| `inspection_baseline` | Create/check a baseline so a change is judged by the findings it introduced, not the existing backlog |
| `configure_inspection` / `configure_formatting` | Write diagnostic severities and any other `.editorconfig` option at solution/project/file scope |
| `suppress_finding` | Silence one finding with a `#pragma` or `SuppressMessage`, compiler-gated |
| `analyze_dependencies` | Project dependency graph, cycle detection, root projects |
| `find_unused_symbols` | Public/internal symbols with zero references (dead code) |
| `get_metrics` | Cyclomatic complexity, lines of code, nesting depth per method |
| `find_code_smells` | Static detection: `async void`, empty catch, await-in-lock, long methods, etc. |
| **Razor pages & views** | |
| `razor_pages` | Inventory of `.cshtml` / `.razor` files: kind (page, view, layout, partial, Blazor component), `@page` route, `@model`, layout, sections, `@inject` services, handlers, code-behind and markup counts |
| `analyze_razor_pages` | Markup, accessibility and maintainability problems with rule id, severity, `file:line` and a fix: unclosed or mismatched tags, duplicate ids/attributes, nested forms, missing `alt`/`lang`/viewport, unlabelled controls, controls without a name, buttons without a type, deprecated HTML, `ViewBag`/`ViewData`, legacy `Html.Partial`, oversized code blocks, forms without an action |
| `find_razor_vulnerabilities` | CWE-tagged security audit: `Html.Raw` XSS, model data in `<script>` or an inline handler, `javascript:` URIs, POST forms without an anti-forgery token, passwords over GET, `target=_blank` without `noopener`, hardcoded credentials, `http://` subresources, data-driven iframes, inline script/style that defeats CSP |
| `razor_edit` | The write side: `set-attribute` / `remove-attribute` on the nth element with a tag, `add-directive` after the last directive, or `insert-lines` / `replace-lines` / `delete-lines` by line range. `expect` verifies the current text, the file is re-scanned before writing (an edit that would unbalance the markup is refused), the result reports the rule findings it resolved and introduced, and the write is recorded in the edit history so `revert_edit` undoes it |
| **XAML** | |
| `xaml_pages` | WPF / WinUI 3 / MAUI / Avalonia / UWP inventory: kind, `x:Class` resolved to its code-behind type, markup counts |
| `analyze_xaml` | `XAML0001`–`XAML0009`: duplicate names and keys, missing or unresolvable `x:Class`, missing event handlers and resource keys, `DataTemplate` without a type, invalid XML |
| `xaml_navigate` | Follow an `x:Name`, resource key, style `BasedOn` chain or event handler to every declaration and use |
| **Documentation** | |
| `get_xml_docs` | XML documentation comment for a symbol, parsed into structured fields |
| `find_undocumented_public_api` | Public/protected symbols missing XML docs |
| `read_project_docs` | The solution's README and Markdown docs, indexed when the solution opened |
| `generate_docs` | Write the missing XML `<summary>`/`<param>`/`<returns>` entries from the declaration name |
| **Refactoring** | |
| `preview_rename` | Preview a symbol rename across the solution without applying it |
| `apply_rename` | Apply a symbol rename across the solution |
| `make_static` | Instance member → static, rewriting every call site |
| `function_to_property` | Parameterless method → property, rewriting every call |
| `property_to_auto` | Trivial accessors → `{ get; set; }`, dropping the backing field |
| `static_to_extension` | Static method → extension method, rotating the receiver (adds the `using` if needed) |
| `introduce_field` | Hoist a context-free expression into a `private readonly` field |
| `introduce_parameter` | Turn a compile-time-fixed expression into a parameter, passing it at every call site |
| `introduce_variable` | Extract an expression into a local variable |
| `extract_method` | Extract a statement block into a new private method (an awaiting block becomes `async Task`; no out-values, no jumps) |
| `change_signature` | Reorder, add or remove parameters, rewriting every call; per-call-site preview, refuses method groups, gated on the compiler |
| `invert_if` | Swap the branches of an if/else and negate the condition |
| `add_params` | Add `params` to the last array parameter |
| `type_to_partial` | Mark every declaration of a type `partial` |
| `change_nullability` | Toggle a nullable annotation, refused when it would add nullability diagnostics |
| `rename_tuple_component` | Rename a named-tuple element, semantically (unrelated `.Name` untouched) |
| `global_usings` | Move a project's plain `using` directives into one `GlobalUsings.cs` (refused if that would break the build) |
| `change_type` | Replace a declaration's type, written only when the project still compiles |
| `move_type_to_outer_scope` | Hoist a nested type to its namespace, rewriting `Outer.Inner` |
| `copy_type` | Duplicate a type under a new name (constructors renamed) |
| `constructor_to_factory` | Constructor becomes private, a static `Create` takes over, `new T(...)` rewritten |
| `convert_to_primary_constructor` | Parameters move into the type header, assignments become field initializers |
| `use_base_type` | Replace a declaration's type with one of its base types, if the project still compiles |
| `push_members_down` | Move a member into the only derived type (refused when the base uses it) |
| `extract_class` | Move a self-contained set of members into a new class, leaving forwarding members behind |
| `extract_interface` | Generate an interface from a class's public instance surface and add it to the base list |
| `extract_superclass` | Move self-contained members into a new base class |
| `encapsulate_field` | Turn a field into a property and rewrite every reference |
| `convert_to_record` | Convert a class or struct to a record, optionally positional |
| `generate_members` | Generate constructor, `Equals`/`GetHashCode` and `ToString` from fields and properties |
| `implement_members` | Generate stubs for the interface and abstract members a type still owes |
| `create_from_usage` | Create the method, property or type a compiler error already assumes |
| `code_cleanup` | Run style → usings → regions → reorder → format in one pass, preview by default |
| `format_code` | Reformat with the project's `.editorconfig`, preserving line endings |
| `simplify_code` | Apply the IDE's mechanical style suggestions at document, project or solution scope — 87 rule ids from Microsoft's code-style table (collection expressions, `??`/`??=`, pattern matching, expression-bodied members, conditional expressions, object initializers, switch expressions, exhaustive-switch completion, LINQ chains, `using` declarations, `static` functions that capture nothing, `HashCode.Combine`, dead code, unused members and unread fields (read/write analysis), dead stores (data flow), unused parameters across every call site, nullability- and trivia-level fixes, async methods that never await, `.editorconfig`-driven braces/modifier order/accessibility/namespace form/foreach cast, `^`/ranges, `nameof`, …), preview by default, compiler-gated, and every write recorded in the solution's edit history |
| `simplify_vb_code` | The same engine for Visual Basic: 15 rule ids from the VB column of the table (simplify name/member access, remove `Me.`, unnecessary cast, predefined type, `Is Nothing`, null-coalescing `If(a, b)`, `?.`, parentheses, compound assignment, boolean equality, conditional simplification, `IsNot` driven by `visual_basic_style_prefer_isnot_expression`, `ByVal` removal), 34 non-rewritten ids answered with the reason, preview by default, compiler-gated. Separate from `simplify_code` because the engine registers one style service per language |
| `extract_method_candidates` | Identify code blocks in long methods suitable for extraction |
| **Orchestration** | |
| `run_plan` | Run a whole workflow inside the server with no model in the loop: a dependency graph of steps executed natively, concurrently where the graph allows it, in one round trip instead of one call per step. A step is a `tool` call — any tool this server exposes, by name, with the argument names its own schema uses — or a shell `command`. Per step: `dependsOn`, `paths` (two steps whose paths overlap never run at the same time, which is what makes parallel edits safe), `forEach` + `as` (fan one step out over a list, `${item}` substituted), `timeoutSeconds`, `continueOnError`. The plan is validated before anything runs — duplicate id, unknown tool, unknown dependency or a cycle rejects it with nothing executed — a failed step skips its dependents unless the failure is declared tolerable, and the report aggregates every step's status, timing, exit code, error and output under a per-step budget |
| **Testing** | |
| `run_tests` | Execute tests via `dotnet test`, parse TRX output, return structured results |
| `get_test_coverage_map` | Per-class and per-method line coverage via XPlat Code Coverage |
| `run_test` | Run one test or one class without hand-building `--filter`; structured TRX result |
| `rerun_failed` | Re-run exactly the tests that failed in the previous run |
| `tests_covering_symbol` | Which test methods reference a symbol, semantically, with `file:line` |
| `generate_tests` | Scaffold an xUnit test class, one `[Fact]` per public method; compiler-gated |
| `test_impact_analysis` | Which tests a change can affect: git diff → changed symbols → referencing tests |
| **Profiling** | |
| `profile_processes` | List the running .NET processes that expose a diagnostics endpoint (pid, name, path, working set) |
| `profile_tools` | Status, install and update for dotnet-trace, dotnet-counters, dotnet-gcdump, dotnet-dump, dotnet-stack, dotnet-monitor and dotnet-symbol — with what VisualMCP covers natively and what is still delegated |
| `profile_collect` | Collect a real `.nettrace` through EventPipe: cpu/gc/exceptions/threading presets or custom providers, bounded collection or start/stop sessions |
| `profile_analyze` | CPU hotspots and call stacks (CPU ms + `file:line`), allocation sites, GC pauses, exceptions, contention, thread-pool backlog, JIT time, rule-based findings and next steps |
| `profile_counters` | EventCounter time series (System.Runtime, ASP.NET Core, Kestrel, HTTP, sockets, gRPC) with min/max/mean/last and raw points |
| `profile_stacks` | Every managed thread's stack, contended monitors with the holding thread, blocked-thread findings |
| `profile_heap` | Managed heap by type, LOH and pinned bytes, finalizable objects, largest objects, duplicated strings |
| `profile_retention` | Why a type is still alive: shortest GC-root chains (each frame names the holding field) and the referrer types |
| `profile_dump` | Collect a dump (`normal`/`withheap`/`triage`/`full`) and analyse an existing `.dmp` (heap + stacks) with ClrMD |
| `profile_symbols` | Read PE debug records, download PDBs from a symbol server into the solution cache so frames resolve to source |
| `profile_monitor` | Start, probe (HTTP) and stop a local dotnet-monitor (`--no-auth`, loopback only) |
| `profile_allocations` | Attribute sampled allocation bytes to the allocating method, type and `file:line`, with the full allocation stacks |
| `profile_calltree` | CPU call tree with inclusive/exclusive time per node, plus optional folded-stacks export (FlameGraph/speedscope) |
| `profile_hotpaths` | Hot paths like VS's view, but several at once: the ranked method/line fix list, the root-to-leaf paths, and the branch points where cost splits; CPU, allocations, exceptions or mixed |
| `profile_hotlines` | Which source line inside a method burns the CPU, resolved from the sampled instruction pointer through IL and the PDB |
| `profile_io` | Where threads are blocked in I/O: File, Http, Network, Database, Pipe, Console, SyncOverAsync, Lock, Sleep, WaitHandle — with call site, source, blocked ms, share and threads |
| `profile_compare` | Diff two traces or two heap snapshots: per-method CPU, per-type allocations/heap, GC, exceptions, contention; regression findings |
| `profile_diagnose` | One call: counters + trace + stacks + heap, every rule, correlated findings ("CPU saturated and method X owns 71%"), one report |
| `profile_slowdown` | Answer "why is this process slow?" with one verdict: cpu/gc/io-wait/sync-over-async/lock-wait/blocked-native scored as the share of the measured window each can explain, ranked evidence, the runners-up and why they lost; live or from an existing trace |
| `profile_memory_growth` | Answer "why is memory growing?": growth/shrink/stable/single-snapshot from two heap JSONs or a live snapshot, with the type the heap grew through correlated to the allocating `file:line` when a gc trace is supplied |
| `profile_threadpool` | Answer "is the thread pool starved?": starved/at-risk/burst/healthy/inconclusive — it requires a full queue *and* threads that cannot take the work, and names the blocking site |
| `profile_review_change` | Answer "is this change a regression?": regression/improvement/unchanged/inconclusive on per-second normalized rates with a significance floor per metric, refusing to judge windows under 500 ms |
| **Debugging** | |
| `debug_launch` / `debug_attach` | Start a built program under netcoredbg, or attach to a running process |
| `debug_breakpoints` | Set line, conditional and hit-count breakpoints; reports which the runtime verified |
| `debug_continue` / `debug_step` / `debug_pause` | Continue, step over/into/out, or break into the process |
| `debug_stack` / `debug_variables` / `debug_evaluate` | Call stack with `file:line`, locals/arguments/autos, watch expressions |
| `debug_threads` / `debug_exceptions` / `debug_output` | Threads, exception breakpoints, console output |
| **Metacode** | |
| `metacode_translate` | Lower C# source, a Razor page, a Ghidra headless snapshot or canonical pseudocode into the one pseudocode language, with `RAW` constructs and their diagnostics listed after the text |
| `metacode_compare` | Translate two sources (any mix of C#, Razor, a Ghidra snapshot or pseudocode) and diff the canonical text line by line — the "is this the same algorithm?" check |
| `decompile_to_metacode` | Decompile a .NET assembly with ILSpy and print it as canonical pseudocode instead of C#: one type, or a paged, filtered set of types, each with its members/functions counted and its diagnostics |
| `analyze_native_binary` | Recover a native binary's functions with Ghidra headless and print them as pseudocode with prototype and entry address; one function or the first N, and the snapshot can be re-read later without Ghidra |
| **Planning** | |
| `board_add` | Create a kanban card (column, priority, labels, assignee) in the solution's SQLite database |
| `board_update` | Edit a card's fields; every change is recorded in its history |
| `board_move` | Move a card across `backlog → todo → in_progress → review → done`, optionally at a position |
| `board_comment` | Append a comment (progress note, blocker, decision) to a card |
| `board_list` | Filter cards by column, priority, assignee, label or text |
| `board_show` | One card with all its comments and its full change history |
| `board_board` | The whole board as five columns, with counts and the done share |
| `board_delete` | Delete a card together with its comments and history |
| `create_task_checklist` / `update_task_checklist` | The lighter, per-task checklist |
| **Git** | |
| `git_status` / `git_diff` / `git_log` / `git_stage` / `git_commit` | The everyday flow |
| `git_branch` | list, create, delete, rename, switch |
| `git_squash` | Fold the last N commits into one (works even when N is the whole branch) |
| `git_amend` | Reword the last commit, keeping its tree |
| `git_merge` | merge with default/no-ff/ff-only/squash, or abort; reports conflicts |
| `git_rebase` | start, continue, abort, skip |
| `git_cherry_pick` / `git_revert` | Copy commits onto the branch, or undo them with new commits |
| `git_reset` | soft/mixed/hard — hard requires `confirm=true` |
| `git_stash` | push, list, pop, apply, drop, show, clear |
| `git_tag` / `git_remote` | Tags (annotated when messaged) and remotes (list/add/remove/set-url) |
| `git_show` / `git_blame` | One commit in full; who last touched each line |
| `git_sync` | fetch, pull, push (force-with-lease behind `confirm=true`) |
| `git_run` | Escape hatch: any git command, verbatim, with exit code and output |
| **GitHub** | |
| `gh_repo` | list, view, create, fork, clone, edit, delete (delete behind `confirm=true`) |
| `gh_pr` | list, view, create, merge, close, reopen, comment, diff, checkout, ready |
| `gh_issue` | list, view, create, close, reopen, comment |
| `gh_release` | list, view, create, upload assets, delete |
| `gh_actions` | runs list/view/logs, rerun, cancel, workflows, dispatch |
| `gh_secret` | list, set, delete (set/delete behind `confirm=true`) |
| `github` | Escape hatch: any `gh` command, verbatim |
| **Docker** | |
| `docker_status` / `docker_ps` | Engine availability and containers (running, or all with `all=true`) |
| `docker_inspect` / `docker_logs` | A container's full configuration; its logs (tail, timestamps, since) |
| `docker_control` / `docker_rm` | start, stop, restart, pause, unpause, kill; remove (force, volumes) |
| `docker_run` | Create and start a container with name, ports, env, volumes, network, command |
| `docker_images` / `docker_pull` / `docker_image_rm` | List or inspect images, pull, delete |
| `docker_networks` / `docker_volumes` / `docker_stats` | The rest of the engine's inventory and one-shot resource stats |
| `docker_compose` | up, down, ps, logs, restart, stop, start, pull, build |
| `docker_prune` | Reclaim disk; refuses unless `confirm=true` |
| **Platform & maintenance** | |
| `host_system` | OS family (windows/linux/macos), architecture, default newline, case-sensitivity, and the line-ending style resolved for a given path (file → `.editorconfig` → `.gitattributes` → sibling files → OS) |
| `fix_line_endings` | Normalise line endings at file/directory/project/solution scope: `auto` fixes mixed files to their majority, `lf`/`crlf` force the whole scope. Every other write already normalises to the target file's own convention, so this is only for a deliberate migration |
| `command_log` | Read the command log kept in the solution's SQLite database: one row per tool call (date, time, tool, arguments), written as the call arrives |
| `edit_history` | Read the solution's edit history: one row per file this server wrote, with the tool, the rules applied and content hashes before/after — an audit trail and the anchor for a revert |
| `revert_edit` | Undo one of those writes by id: the previous text is kept compressed, so the file is restored exactly; it refuses when the file changed after the edit |
| `reference_index` | The solution's symbol index, persisted in `.visualMCP/visualmcp.db`: `build` re-indexes only the files whose content hash changed; `find`/`describe` locate a member (signature, documentation, exact span); `refs` lists every use with HOW it is used — call, read, write, instantiate, inherit, implement, override, attribute, cast, `typeof`, `nameof`, pattern, type argument — and the member it happens inside; `callers`/`callees` are the two halves of the call hierarchy; `hierarchy` gives base types, interfaces, derived types, implementers and overrides; `outline` lists a type's members or a file's declarations; `metacode` returns the metacode tree of a declaration (or every tree of a file), generated when the document was indexed; `unused` lists non-public declarations nothing uses |
| `package_facts` | The resolved NuGet graph from `obj/project.assets.json` (no restore, no network): every package per target framework with its version and whether it is a direct reference, plus the dependency edges |
| **Modernization & upgrade** | |
| `aspire` | Install and scaffold .NET Aspire on demand: status (SDK band, templates, workload, existing AppHost/ServiceDefaults), install (templates, no elevation; the workload on .NET 8 or on request), add (AppHost + ServiceDefaults projects, solution entries, project references, `AddProject<T>` and `AddServiceDefaults()` wiring, then a build that reverts the wiring if it fails) |
| `update_framework` | Move the projects to a newer target framework: in-place value edits, platform suffixes kept, `netstandard`/`net4x` left alone, `global.json` kept in sync, preview by default, and a build gate that restores every edited file on failure |
| `update_solution` | The framework bump plus every outdated top-level NuGet package in one gated pass — project files or `Directory.Packages.props` rows, preview by default, full rollback when the build fails |
| **EF Core** | |
| `ef_migrations` | Run the project's own `dotnet ef`: `list`, `add`, `script`, `remove`, `update`, `info`, `dbcontext-list`, with the exact command and structured output |
| **Scaffolding & protocols** | |
| `add_license` / `list_licenses` | A LICENSE from 20 bundled templates; the catalogue with SPDX identifiers, copyleft family and patent grants |
| `add_contributing` | CONTRIBUTING.md, optionally with a relicensing-friendly CLA |
| `add_ci_workflow` | A GitHub Actions workflow: multi-OS build, or tagged self-contained release binaries |
| `http_invoke` | Call a REST endpoint of a running app: any method, headers, JSON body, timeout |
| `signal_r_*` | Connect to an ASP.NET Core SignalR hub, subscribe, invoke and drain events |

### Engine status

Which IDE rules are rewritten, which are not and why, the four structural limits of the rewrite engine
and the state of the solution database: [docs/engine-state.md](docs/engine-state.md). The rule-by-rule
table is in [CAPABILITY_MAP.md](CAPABILITY_MAP.md), the comparison against ReSharper/Rider/CodeMaid in
[docs/resharper-codemaid-coverage.md](docs/resharper-codemaid-coverage.md), and **everything the product
does not do** in [docs/gaps.md](docs/gaps.md): the remaining language and platform gaps (JS/TS, SQL as a
language, Kubernetes, F# depth, the parts of XAML and EF Core that stay markup-only), test debugging and
mutation testing, and the remaining template and inspection-administration gaps. The other backlog —
**how many calls an agent needs to answer one question**, measured on the session store, with the
chains that are still mechanical — is [docs/agent-ergonomics.md](docs/agent-ergonomics.md). The engine is a
language-neutral core plus a per-language layer, so a new language only supplies its matchers:
[docs/adding-a-language.md](docs/adding-a-language.md).

### Snitch.Metacode

`SNITCH/Libraries/Snitch.Metacode` lowers syntax trees into one pseudocode model: C# through Roslyn
(syntax only — no compilation, no solution), Razor pages, and the C Ghidra reconstructs from a native
binary. Python (a CPython `ast` JSON dump) and C++ (`clang -ast-dump=json`) have readers too, but they
are opt-in: nothing in this product produces those dumps, so the default pipeline does not claim the
languages. The same algorithm written in any of them prints the same body, line for line; a construct
the model has no node for becomes `RAW[lang] …` plus a diagnostic instead of silently disappearing. The
canonical text is also a frontend, so any other language joins by emitting it. Four tools expose it:
`metacode_translate` (one source → pseudocode + diagnostics) and `metacode_compare` (two sources →
pseudocode and a line diff) work on text, `decompile_to_metacode` turns an assembly's ILSpy output into
the same shape, and `analyze_native_binary` runs Ghidra headless and returns the recovered functions as
pseudocode (the snapshot it writes can be re-read by `metacode_translate` with no Ghidra). None of them
needs a loaded solution. Visual Basic has no frontend, and the tool says so instead of returning a
parse error. Usage, the grammar and the known limits: [docs/metacode.md](docs/metacode.md).

### Web dashboard

The server hosts a read-only web view of itself on **http://127.0.0.1:4677** — the process
(pid, uptime, memory, runtime, transport, loaded solutions), the tool catalogue it advertises,
the tail of `debug.log`, and **one page for every table of the solution store**
(`<solution>/.visualMCP/visualmcp.db`): the command log, the edit history, the board, the symbol
and full-text indexes, package facts, decompiled artifacts — plus any table a future feature
adds, because the list comes from `sqlite_master` and not from a hard-coded one. Tables page with
substring filtering, sortable columns and `CSV`/`JSON` exports; the log page refreshes itself.

It runs inside the MCP process, so there is nothing extra to start. The port is **4677**, else the
port it last bound (`dashboard.port` in the store's `context` table), else one the OS assigns — so
a second MCP client on the same solution gets its own stable port instead of colliding. It binds
loopback only, every route is a `GET`, and a port that cannot be bound is a warning in the log,
never a failure of the MCP session. `VISUALMCP_DASHBOARD=off` disables it,
`VISUALMCP_DASHBOARD_PORT` / `VISUALMCP_DASHBOARD_HOST` move it.
Pages, query parameters, the JSON API and the safety rules: [docs/dashboard.md](docs/dashboard.md).

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows (MSBuildLocator discovers the SDK installed via `dotnet.exe`)

## Installation

The install script builds a Release executable, publishes it into the client's
conventional folder, and registers it as an MCP server in **ZCode** and/or
**Claude Code** at the **scope you choose**. After any install/uninstall,
**restart the client** so it reloads its configuration.

### Choose a scope

| Scope | Availability | Stored in | Use when |
|-------|--------------|-----------|----------|
| `all` *(default)* | ZCode (user) + Claude Code (user) + Claude desktop app | both configs below | You run more than one client |
| `zcode` | Every ZCode session on this machine — global | `%USERPROFILE%\.zcode\cli\config.json` → `mcp.servers` | You only use ZCode |
| `user` | Every folder on this machine — global | `%USERPROFILE%\.claude.json` | Claude Code only, everywhere |
| `local` | Only the chosen project folder, private to you | `%USERPROFILE%\.claude.json` (keyed to that folder) | You want it for one project without affecting others or your team |
| `project` | The chosen project, **shared** via a committed file | `.mcp.json` in the project | You want teammates to get it too |

```powershell
# ZCode + Claude Code (default)
powershell -ExecutionPolicy Bypass -File scripts\install.ps1

# ZCode only — registers %USERPROFILE%\.zcode\cli\config.json -> mcp.servers.VisualMCP
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode

# Claude Code only, global
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope user

# Just this one project, private to you (run from the project, or pass -ProjectDir)
cd C:\REPOSITORY\MyApp
powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope local

# Shared with the team via a committed .mcp.json
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope project -ProjectDir C:\REPOSITORY\MyApp
```

The exe is published to `%USERPROFILE%\.zcode\mcp-servers\VisualMCP\app\` for
ZCode and `%USERPROFILE%\.claude\mcp-servers\VisualMCP\app\` for Claude Code
(one build, copied). `install.ps1` in the repository root is a thin wrapper that
additionally supports `-SetupProject`, which writes the "use MCP tools, not
Grep" rules into **both `CLAUDE.md` and `AGENTS.md`** of a target project. It uses
the same managed block as the user-level guard (`hooks\agents-block.md`), delimited
by `<!-- VisualMCP:begin/end -->`, so re-running replaces it instead of duplicating
it and the rest of the file is left alone:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -SetupProject C:\REPOSITORY\MyApp -SolutionPath C:\REPOSITORY\MyApp\MyApp.sln
```

To remove it (match the scope you installed with):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope all   -Uninstall
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode -Uninstall
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope local -Uninstall -ProjectDir C:\REPOSITORY\MyApp
```

> **ZCode specifics:** ZCode reads MCP servers from `mcp.servers` in
> `%USERPROFILE%\.zcode\cli\config.json` (user scope) or `<repo>\.zcode\config.json`
> (workspace scope). Its file schema is strict — unknown keys drop the server —
> and the JSON must be **BOM-free**; the script writes it that way. Verify the
> connection in **Settings → MCP**; tools surface as `mcp__VisualMCP__<tool>`.

> **`project` scope and teams:** the generated `.mcp.json` records this machine's
> absolute path to the published `.exe`. Teammates on other machines must run the
> script themselves (the path won't exist for them). For a fully portable team
> setup, commit a `dotnet run`-based `.mcp.json` instead (see *Development* below).

---

### Routing guard: the hook that stops `grep`/`sed`/`ls` on .NET repos

ZCode does **not** forward an MCP server's `instructions` to the model, so the server
cannot ask for its own tools to be preferred. Registering the server is therefore not
enough: a model that has never been told otherwise keeps doing its C# work with
`grep -rn`, `sed -n`, `cat`, `ls`. The install ships two independent pieces that close
that gap. Both are user-level, idempotent and removable.

| Piece | What it does | Where it is registered |
|---|---|---|
| `hooks\pre-tool-use.ps1` | `PreToolUse` hook on the `Bash` tool. Denies read-only probes — `grep`, `rg`, `sed -n`, `cat`, `head`, `tail`, `ls`, `find`, `Select-String`, `Get-Content`, `Get-ChildItem` — and every shell file write (inline `python` with a write call, `sed -i`, `cat >`/`tee`/`echo >`), when a `.sln`/`.slnx` is visible within 3 levels of the working directory. Replies with the tool to call instead (`find_symbol`, `search_text`, `read_file`, `list_directory`, or `Edit`/`Write`) | ZCode: `%USERPROFILE%\.zcode\cli\config.json` → `hooks.events.PreToolUse` + `hooks.enabled: true`<br>Claude Code (`-Scope all`): `%USERPROFILE%\.claude\settings.json` → `hooks.PreToolUse` |
| `hooks\agents-block.md` | The same routing rules stated up front: what to use instead of grep/sed/ls, and why C# semantics (overloads, overrides, generics, partial types) need Roslyn rather than text matching | `%USERPROFILE%\.zcode\AGENTS.md` — user scope, loaded in every workspace. Claude Code needs nothing here: it does receive the server's own `instructions` |

Build, test, git, file writes and non-.NET trees are never touched: the hook passes
anything that mentions `dotnet`, `git`, `npm`, or that redirects to a file. Two escape
hatches exist for the rare genuine shell case: the MCP tool `run_command` (same command,
still visible in the plugin log) and the literal token `vmcp-allow` inside the command.

```powershell
# Refresh the guard after editing the hook or the block — no republish needed
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope zcode

# Install it for Claude Code too (user-level hooks)
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope all

# Remove hook + AGENTS.md block, or skip them during an install
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Uninstall
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -NoHooks
```

> Hooks are read at **session start**: restart ZCode / Claude Code after installing.
> The hook never fails a session — an unparsable payload or an internal error exits 0
> (pass). `hooks.enabled: true` is required for ZCode configuration-file hooks; plugin
> hooks would enable the runner by themselves, but VisualMCP is registered as a plain
> MCP server, not as a ZCode plugin.

**Why the install script is better than `dotnet run`**

| | `dotnet run --project ...` | installed exe |
|---|---|---|
| Startup | slow — compiles on every launch | fast — pre-built Release binary |
| Output | build text can corrupt the stdio protocol | clean |
| After a code change | picks it up automatically | requires re-running the install script |

The `.mcp.json` at the root of this repo points at the built **Debug** DLL for
development convenience. The install script produces a stable Release build that
is better suited for daily use.

---

### Manual quick start

If you want to run it without the script, build it:

```powershell
dotnet build src/VisualMCP/VisualMCP.csproj
```

Then add to your project's `.mcp.json` (point at the built DLL — running the
assembly directly avoids `dotnet run`'s build output corrupting the protocol):

```json
{
  "mcpServers": {
    "VisualMCP": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "C:\\REPOSITORY\\VisualMCP\\src\\VisualMCP\\bin\\Debug\\net10.0\\VisualMCP.dll"
      ]
    }
  }
}
```

The solution in the working directory is **auto-discovered and loaded on demand**,
so you can call any tool right away — no explicit `load_solution` step needed.

### Use the `/vs-load` skill

```
/vs-load C:\REPOSITORY\MyApp\MyApp.sln
```

Or scan a directory:

```
/vs-load C:\REPOSITORY\MyApp
```

## Tool reference

### `list_solutions`

```
rootPath   – directory to scan (e.g. C:\REPOSITORY)
maxDepth   – recursion depth (default: 3)
```

### `load_solution`

```
path       – absolute path to the .sln or .slnx file
```

Loads the solution into memory. **Usually unnecessary** — tools auto-discover and load the working-directory solution on first use. Call this only to target a specific `.sln`/`.slnx` by path, or if a tool reports none could be located. Returns project list and workspace diagnostics (MSBuild warnings/errors).

### `get_project_info`

```
nameOrPath – project name (as returned by load_solution) or absolute .csproj path
```

Returns: assembly name, language, output type, source file list, project references, assembly references, NuGet packages.

### `find_symbol`

```
symbolName   – name to search for
partialMatch – if true, matches any symbol whose name *contains* symbolName (default: false)
```

Uses `SymbolFinder.FindSourceDeclarationsAsync` — semantically correct, not regex.

### `read_symbol`

```
symbolName     – type or member name
containingType – optional containing type, to disambiguate overloads or nested types
partialMatch   – if true, matches any symbol whose name *contains* symbolName (default: false)
maxLines       – max lines of source per declaration, 20-1000 (default: 300)
```

Returns the declaration's **source** — the whole type body, method body, property, field — with
line numbers, or each partial declaration for a partial type. This is the one call that replaces
`search_text` + `read_file` when the goal is "show me the code of this name": symbol resolution is
semantic (correct overloads, nested/partial types, no regex false positives), and the result is
plain text, not a JSON string with escaped newlines.

### `find_implementations`

```
interfaceName – full or partial interface name (e.g. "IMyService" or "MyApp.IMyService")
```

Returns every implementing type and, for each, which member satisfies each interface contract (including explicit implementations).

### `run_tests`

```
projectFilter – optional: run only projects whose name contains this string
extraArgs     – optional: forwarded verbatim to dotnet test
                (e.g. "--no-build", "--configuration Release")
```

Runs `dotnet test`, parses all `.trx` files produced, and returns:
- Summary: total / passed / failed / skipped / duration
- Per-project breakdown
- Failed test details: test name, error message, stack trace

### `read_file`

```
path     – absolute or relative file path
fromLine – first line to read (1-based, optional)
toLine   – last line to read (1-based, optional)
```

Returns plain numbered source (a `path (lines X-Y of N)` header, then the lines), so the model reads
code as code. `read_symbol` is the better call when you want a declaration's body rather than a
file range.

### `read_files`

```
paths    – file paths to read, in order (duplicates dropped)
maxBytes – total output budget in characters (2000-190000, default 60000)
```

Same numbered output as `read_file`, one block per file. A file that fails does not abort the
batch: its error is reported in place. When the budget runs out the remaining paths are named at
the end of the result, so the model can ask for them in a second call instead of guessing what it
missed. Built for the case the burst analysis found most often: several files whose paths are
already known, previously paid one round-trip each.

### `read_tree`

```
path               – root directory
depth              – levels of contents to expand (1-8, default 3)
pattern            – glob for file names, e.g. *.cs (default *)
includeBuildFolders – include bin, obj, .git, .vs, .idea, node_modules (default false)
maxEntries         – cap on rendered entries (50-2000, default 600)
```

An indented tree with file sizes and per-folder counts, in one call. Folders deeper than `depth`
are named but not expanded; folders with more than 30 matching files are summarised. This replaces
the `list_directory` ladder (one call per level) that dominated the exploration bursts.

### Profiling

The profiling tools operate on a **running** .NET process, so they take `processId` or
`processName` instead of a solution path. They are native: none of them spawns dotnet-trace,
dotnet-counters, dotnet-gcdump, dotnet-dump or dotnet-stack. `profile_tools` reports what is still
delegated (for example the `.gcdump` file format) and can install the external tools when you need
them. Full detail in [docs/profiling.md](docs/profiling.md).

```
profile_processes(nameFilter?)                       → pids, names, paths, working sets

profile_tools(tool?, action=status|install|update,
              scope=auto|global|local, version?, checkLatest?)

profile_collect(processId? | processName?, profile=cpu|gc|exceptions|threading|runtime|diagnose|custom,
                durationSeconds=10, providers?, bufferMb=256, requestStackwalk=true,
                action=collect|start|stop|sessions, sessionId?, outputPath?)

profile_analyze(tracePath, top=25, resolveSource=true, deep=true, writeReport=true)
                → CPU hotspots + stacks, allocation sites + types, GC, exceptions
                  with throw sites, contention, thread pool, JIT, timeline,
                  findings, next steps, Markdown report path

profile_allocations(tracePath, top=25, focusType?, resolveSource=true)
                → allocation sites (method + type + file:line), top stacks, findings

profile_calltree(tracePath, top=40, maxDepth=12, resolveSource=true,
                 writeFolded=false, outputPath?)
                → inclusive/exclusive call tree, roots, folded-stacks path

profile_hotpaths(tracePath, metric=cpu|allocations|exceptions|mixed,
                 top=10, minPercent=1, resolveSource=true, userCodeOnly=true)
                → ranked user-method fix list, root-to-leaf hot paths,
                  branch points, findings

profile_io(tracePath, top=15, minPercent=1, resolveSource=true)
                → I/O blocking sites by category/call site with blocked ms,
                  share, threads, stack and recommendation

profile_compare(leftPath, rightPath, kind=auto|trace|heap, top=25)
                → metric/per-method/per-type deltas + regression findings

profile_counters(processId? | processName?, countersFilter?, providers?,
                 durationSeconds=10, intervalSeconds=1)

profile_stacks(processId? | processName?, maxFrames=40,
               includeRuntimeFrames=false, writeReport=false)

profile_heap(processId? | processName?, top=25, focusType?,
             includeDuplicateStrings=true, writeJson=false, writeReport=false)

profile_dump(action=collect|analyze, processId? | processName?, outputPath?,
             dumpType=withheap, dumpPath?, top=25, focusType?,
             includeStacks=true, maxFrames=40)

profile_diagnose(processId? | processName?, durationSeconds=10,
                 includeHeap=true, includeStacks=true, top=15)
                → counters + trace + heap + stacks, correlated findings,
                  one Markdown diagnosis report

profile_slowdown(tracePath? | processId? | processName?, durationSeconds=10,
                 includeStacks=true, top=15, resolveSource=true, userCodeRoot?)
                → ONE verdict (cpu | gc | io-wait | sync-over-async | lock-wait |
                  blocked-native | not-slow): the cause, the evidence ranked by the
                  share of the window it explains, the runners-up and why they lost

profile_memory_growth(baselinePath?, currentPath?, processId? | processName?,
                      tracePath?, top=25, resolveSource=true)
                → growth | shrink | stable | single-snapshot, the type the heap grew
                  through, and the allocation site (file:line) when a gc trace is given

profile_threadpool(processId? | processName?, durationSeconds=10,
                   top=15, resolveSource=true, userCodeRoot?)
                → starved | at-risk | burst | healthy | inconclusive, queue/worker
                  numbers, the blocking sites

profile_review_change(baselinePath, currentPath, kind=auto|trace|heap, top=25)
                → regression | improvement | unchanged | inconclusive on per-second
                  normalized rates, the driver, and a refusal when the windows are
                  too short to compare

profile_symbols(action=list|download, modulePaths[], outputDirectory?, symbolServer?)

profile_monitor(action=status|start|stop|probe, url?, configPath?,
                path=/info|/processes|/metrics|/dump?…)
```

Artifacts land in `<solution>/.visualMCP/profiling/` (traces, dumps, reports, symbols); the folder
is already covered by `.gitignore`.

### Metacode

Neither tool needs a loaded solution or a running process: they read a file or a text and print
pseudocode. Python and C++ are read from their **AST JSON dumps**, not from source.

```
metacode_translate(path? | text+language=cs|py|cpp|razor|ghidra|pseudo,
                   showOrigin=false, includeComments=true,
                   normalizeControlFlow=true, flattenNestedBlocks=true)
                → the canonical pseudocode, then `// diagnostics (n)` with one line
                  per RAW construct (MC0001–MC0008)

metacode_compare(leftPath? | leftText+leftLanguage?,
                 rightPath? | rightText+rightLanguage?,
                 showOrigin=false, includeComments=true,
                 normalizeControlFlow=true, flattenNestedBlocks=true)
                → header per side, `-`/`+`/` ` diff of the canonical text with
                  long unchanged runs collapsed, per-side diagnostics after it

decompile_to_metacode(assemblyPath, typeFullName?, nameFilter?, namespaceFilter?,
                      skip=0, maxTypes=0, showOrigin=false)
                → ILSpy → C# → the metacode model, printed as pseudocode; one type
                  or a page of types, with hidden/error counts and diagnostics

analyze_native_binary(binaryPath, function?, maxFunctions=0,
                      ghidraHome?, headlessPath?, javaHome?,
                      timeoutSeconds=300, analysisTimeoutSeconds=240,
                      keepProject=false, showOrigin=false)
                → a real Ghidra headless run, then the recovered functions as
                  pseudocode with prototype + entry address; snapshot and log paths
                  in the header. Re-read the snapshot with metacode_translate
```

`decompile_to_metacode` and `analyze_native_binary` need no solution either, and
`analyze_native_binary` is the only one that starts an external process (Ghidra); the snapshot it
leaves behind is an ordinary metacode input afterwards.

Canonical pseudocode is itself an input (`language=pseudo`, or a `.mc` file), so a language without
a frontend joins by emitting it. `language` is required for text and, with a path, overrides the
extension — an unknown extension alone has no frontend. Visual Basic has no frontend: the tool answers
with that reason.

### Modernization

`aspire` installs and scaffolds Aspire on demand; `update_framework` and `update_solution` move the
solution forward and put every edited file back when the build fails. Full detail in
[docs/modernization.md](docs/modernization.md).

```
aspire(action=status|install|add, appHostName?, projects[]?,
       serviceDefaults=true, installIfMissing=true, verify=true,
       includeWorkload=false, force=false, timeoutSeconds=300)
                → status: SDK band, templates/workload, AppHost/ServiceDefaults/
                  consumer projects, raw probes
                → install: dotnet new install Aspire.ProjectTemplates (+ the
                  workload on .NET 8 or on request)
                → add: AppHost + ServiceDefaults projects, sln entries, project
                  references, AddProject<T>("name") lines, AddServiceDefaults()/
                  MapDefaultEndpoints(), then a build of the AppHost; the
                  Program.cs wiring is reverted when it does not compile

update_framework(targetFramework=auto|net10.0, projectNames[]?, dryRun=true,
                 buildGate=true, updateGlobalJson=true,
                 createGlobalJson=false, timeoutSeconds=900)
                → the plan (file, property, from, to) or the applied bump; a
                  failed build restores every edited file

update_solution(targetFramework?, projectNames[]?, includePrerelease=false,
                dryRun=true, buildGate=true, updateGlobalJson=true,
                createGlobalJson=false, timeoutSeconds=900)
                → framework bump (when asked) + every outdated top-level package
                  (project files or Directory.Packages.props), then one build
                  gate with a full rollback
```

## Architecture

```
src/VisualMCP/  — the MCP host (thin)
  Program.cs                            ← startup; MSBuildLocator.RegisterDefaults() runs
                                          first, then opens the environment in the background
  Tools/**/*.cs                         ← one [McpServerToolType] per file; each forwards to
                                          a Snitch service injected by the MCP SDK
  Dashboard/                            ← the in-process web view (Kestrel on loopback): the
                                          store tables, the tool catalogue, the live log
  Update/                               ← VisualMCP-specific self-update (GitHub Releases)

SNITCH/Libraries/Snitch.Profiling/  — the profiling library (host-agnostic)
  Runtime/                              ← process discovery, EventPipe trace and counter sessions
  Snapshots/                            ← ClrMD: thread stacks, heap, dump collection/analysis
  Analysis/                             ← trace analyzer, portable-PDB source map, advisor, report
  Tooling/                              ← the seven-tool catalogue, detection and installer
  Symbols/ Monitor/                     ← dotnet-symbol and dotnet-monitor equivalents

SNITCH/  — the code-intelligence library (the engine; host-agnostic)
  SnitchContext.cs                      ← the environment: discovers + loads every solution,
                                          exposes the active one; implements IRoslynWorkspace
  SnitchServiceCollectionExtensions.cs  ← AddSnitch(): registers the context + every service
  Workspace/                            ← IRoslynWorkspace, SolutionDiscovery, ProjectDocs
  Languages/CSharp/                     ← Navigation, Analysis, Refactoring, Testing, Packages, Modernization…
  Git/ GitHub/ Docker/ Execution/ IO/ …  ← the I*Service implementations
  Languages/CSharp/Parsing/CsprojParser ← NuGet package names Roslyn's model doesn't expose
```

### Logging

The server appends every incoming JSON-RPC message to
`%USERPROFILE%\.claude\mcp-servers\VisualMCP\debug.log` (fixed path, whichever
copy of the exe is running). `REQ` lines record requests and notifications with
their method, id and params (truncated), plus the duration on completion; tool
invocations additionally get `TOOL` lines with their arguments and outcome.
The logger is installed by `src/VisualMCP/Logging/` (`FileLoggerProvider`,
`ToolCallLogging`, `RequestLogging`) and never writes to stdout, so it cannot
corrupt the stdio protocol. The dashboard's `/log` page tails the same file,
newest first, with a text filter and one for tool calls, requests, errors and
warnings ([docs/dashboard.md](docs/dashboard.md)).

### Auditing calls that never reach the server

The server can only log requests it receives; a tool the model never calls is
invisible to it. ZCode records every model call — prompt, response and chosen
tool calls — in `%USERPROFILE%\.zcode\cli\rollout\model-io-*.jsonl`. Run
`python scripts\audit-mcp-usage.py` to list the turns where C# work used
Read/Grep/Bash while VisualMCP was never called, i.e. missed-intent candidates.
`--json` emits machine-readable output, `--include-implementation` also counts
Write/Edit (off by default, since VisualMCP is an analysis/refactoring server),
and `--rollout-dir` points it at another directory.

### Key design decisions

- **MSBuildLocator must be called before any Roslyn type is JIT-compiled.** `Program.cs` calls it at the top of `Main`, then delegates to a `[MethodImpl(NoInlining)]` method to prevent the JIT from eagerly compiling Roslyn references.
- **`SnitchContext` is the process-lifetime environment.** Opening it discovers and loads every solution under the working directory; one is the *active* solution (switchable via `load_solution`) that tools see through `IRoslynWorkspace`. This matches the single-session MCP model.
- **Solution discovery prunes agent/tooling trees.** `.claude` (Claude Code worktrees), `.zcode`, `.idea` are skipped alongside `bin`/`obj`/`.git`/`.vs`: a repo with three worktrees used to load the same solution four times (Sperone: 17 s warm, ~4 min cold, four copies in memory). Such a solution can still be opened explicitly with `load_solution`.
- **The eager load is skipped for discovery probes.** ZCode starts an extra process per session that only calls `server/discover` + `tools/list`. `ProbeDetectingStream` reads the first JSON-RPC message: `server/discover` means probe (skip the load), `initialize` means a real client (load as before).
- **`Microsoft.Build.*` references must carry `ExcludeAssets="runtime"`** when used alongside `MSBuildLocator`, otherwise the locator refuses to start (it enforces this via a `.targets` check).
- **NuGet packages are read from the `.csproj` XML** (via `CsprojParser`) because Roslyn's `Project` model exposes assembly/metadata references but not the original package names.
- **CPU stacks in a `.nettrace` are attached to the event record, not emitted as events.** A plain `EventPipeEventSource` pass sees the sample events but no stack frames; the stacks become reachable only after TraceEvent converts the file to ETLX. `profile_analyze` therefore does the cheap event pass directly and converts to a temporary `.etlx` (deleted afterwards) only when the trace contains sample-profiler events. CPU comes from the managed `ThreadSample` events (each carries the sampled thread's stack): consecutive samples of a thread bound a CPU interval, and gaps far beyond the sampling rate are dropped. We deliberately do **not** use `SampleProfilerThreadTimeComputer`: it also folds allocation ticks into the CPU stream, so in a Verbose trace the "CPU" landed on the allocating lambda, and a thread sampled only at the start and end contributed a phantom interval equal to the whole trace (a 3 s trace produced 6 s of CPU).
- **Profiling is a separate library, not part of Snitch.** `SNITCH/Libraries/Snitch.Profiling` takes explicit paths and process ids and has no workspace dependency, so it can be used from a test or a console host; the MCP tools supply the solution directory.
- **The web dashboard is a second, in-process host.** `Dashboard/DashboardServer` builds a minimal ASP.NET Core application over the `Microsoft.AspNetCore.App` shared framework and runs it next to the MCP stdio transport. Its logging providers are cleared first — stdout belongs to the protocol — and it is started from the same background task that opens the environment, so binding a port can never delay or fail an MCP session. Its SQLite handle is per query and read-only in intent, so it never competes with the store, the board or the indexes.

## Development

```powershell
# Build
dotnet build src/VisualMCP

# Watch (auto-rebuild on save)
dotnet watch --project src/VisualMCP build
```

Building from Visual Studio also refreshes the installed copies: a post-build
target runs `scripts\install.ps1 -Scope all -StopRunning`, which stops the
running MCP server, publishes Release to the `.zcode` / `.claude` app folders
and re-registers it. The clients relaunch the new server on their next tool
call. Set `VisualMCPInstallOnBuild=false` to opt out.

The MCP server communicates over stdio — there is no HTTP endpoint to test directly. Use Claude Code with the MCP registration above, or write an integration test that sends JSON-RPC over stdin.

## License

VisualMCP is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ **Free for noncommercial use** — individuals, hobby and personal projects, learning, research, education, and other noncommercial organizations may use, modify and share it at no cost.
- 💼 **Commercial / company use requires a paid license.** Any use by or for a company (or for commercial advantage or monetary compensation) needs a separate commercial license. Contact **me@matteofabbri.org**.

This is **source-available**, not OSI "open source": you can read, fork and contribute, but commercial use is reserved. See [`LICENSE`](LICENSE) for the exact terms.

*This summary is for convenience only and is not legal advice; the [`LICENSE`](LICENSE) file governs.*
