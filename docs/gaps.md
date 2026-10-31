# What is missing — the gap inventory

This is the complete list of what VisualMCP does **not** do. It complements
[ReSharper & CodeMaid — Feature Coverage](resharper-codemaid-coverage.md), which is the ✅ side: every row
here is a feature a user could reasonably expect from an IDE and will not find in this product.

A different axis — how many calls an agent needs to answer one question, measured on the session
store — is [Agent ergonomics](agent-ergonomics.md). That page is a backlog of tool-surface work;
this one is the feature inventory.

Three kinds of absence are kept apart, because they need different decisions:

| Mark | Meaning | What to do about it |
|---|---|---|
| ❌ | A real gap. The feature exists in ReSharper/Rider and could be built here. | Implement it; section 13 says where the remaining boundaries are |
| ⚠️ | Partial. Something exists, and the row says exactly what is missing. | Extend the existing tool |
| 💀 | Not applicable by design. The feature belongs to an editor UI, not to a headless MCP server. | Nothing — the client supplies the UI |

Last updated: 2026-10-31 (fifteenth revision: **the last ReSharper/CodeMaid rows in the coverage
page are closed**. `templates` (18 live, 10 postfix and 8 file templates, plus a team store in
`.visualMCP/templates.json`) and `layout_code` (CodeMaid's comment reflow, line joining and blank-line
rules) are new tools; `code_cleanup` takes a named **profile** (built-in and team, `saveProfileAs`
writes one), `find_symbol` falls back to a fuzzy subsequence, `create_from_usage` covers CS0117 and
CS1501/CS7036 next to the original three, and `context_action` gained ten ids (merge/split conditions,
join a null check with its use, `var` and back, split declaration, remove braces, nullable migration,
digit separators, `ConfigureAwait(false)`). **373 → 375 tools.** The fourteenth revision closed section
13 — the workaround table is gone because each of its rows became either a tool or a documented
boundary. New families: **Kubernetes and Helm** (`k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`,
`k8s_logs`, `k8s_apply`, `k8s_delete`, `k8s_rollout`, `k8s_helm` — 9), **Node / JavaScript / TypeScript**
(`node_info`, `node_install`, `node_script`, `node_typecheck`, `node_lint`, `node_outdated`,
`node_audit` — 7, with `tsc` and `eslint` parsed into diagnostics) and **SQL** (`sql_files`,
`analyze_sql` with SQL0001–SQL0010 — 2). The thirteenth revision closed section 1.2 — Visual Basic is no
longer only a style layer. The symbol index, `structural_search`,
`format_code`/`optimize_usings`/`code_cleanup`, the inspection packs that ship VB analyzers, dead code /
code smells / async / performance / complexity and the refactorings that are not C#-idiom-specific now
read `.vb`; no new tools there, the same build covers both languages, and that batch also closed
section 9 — `git_conflicts` (the conflict model), `git_rebase action=plan` (interactive rebase as data),
`git_patch` (named shelves), `git_host` (GitLab / Azure DevOps / Bitbucket), `gh_pr action=review` with
line-anchored comments + `action=checks`, `gh_branch_protection` and `code_review` — and section 7
closed `make_non_static`, `transform_parameters`, `move_type_to_nested_scope` and `generify`
(346 → 355). The style counts (97 C# ids, 15 VB ids) are read from the catalogues; the inspection
figure is the documented ~800 from five packs.

---

## At a glance

| Area | State | Priority | Why this priority |
|---|---|:---:|---|
| [Language and platform scope](#1-language-and-platform-scope) | ⚠️ | Low if your solution uses them | C#, VB, XAML, Razor, EF Core, SQL (scanner), Kubernetes and the Node toolchain are covered; a JS/TS or F# codebase still has no semantic model |
| [Debugger](#2-debugger) | ✅ | — | Implemented: 16 `debug_*` tools over netcoredbg (DAP). The engine is here; the window is the client's ([section 11](#11-ui-bound-features--not-applicable-by-design)) |
| [Inspection administration](#3-inspection-administration-and-background-analysis) | ✅ | — | Severity, suppression, custom rules, `.DotSettings` reading, naming rules, spelling and a warm analysis loop exist; only the proprietary ReSharper id set is absent |
| [Test workflow](#4-test-workflow) | ✅ | — | Running, discovery, debugging a test, continuous testing, affected tests, flaky detection and mutation testing all exist; only the explorer UI is the client's |
| [Decompiled code as a workspace](#5-decompiled-code-as-a-workspace) | ✅ | — | `assembly_workspace` decompiles into a Roslyn workspace: search, source, references, outline |
| [Context actions and templates](#6-context-actions-templates-and-generation) | ⚠️ | Low | 36 action ids and the template family (18 live, 10 postfix, 8 file) now exist; what remains is the proprietary long tail (LINQ shape, StringBuilder, extension blocks, data-flow predicates) |
| [Refactoring gaps](#7-refactoring-gaps) | ✅ | — | All ~51 of Rider's main set are implemented, including `transform_parameters`, `make_non_static`, `move_type_to_nested_scope` and `generify` (2026-10-31) |
| [Diagrams and exploration](#8-architecture-diagrams-and-exploration) | ✅ | — | Graph artifacts (project/namespace/type, call, sequence, control flow), value tracking, localization and the explorers exist as data; drawing them is the client's |
| [Version control and collaboration](#9-version-control-and-collaboration) | ✅ | — | Git, the conflict model, plan rebase, patches, all four hosting providers and the review workflow exist; only real-time co-editing is out of scope by design |
| [Build, run, packaging](#10-build-run-and-packaging) | ⚠️ | Low | Build/test/run/publish exist; package search is covered; installers and deployment targets do not |
| [UI-bound features](#11-ui-bound-features--not-applicable-by-design) | 💀 | — | Deliberately out of scope for an MCP server |

---

## 1. Language and platform scope

The semantic engine is **Roslyn**, and it reads the two languages a .NET solution can be written in:
**C#** and **Visual Basic**. Everything else is one of four things:

- a dedicated **Razor/Blazor markup scanner** (heuristics over `.cshtml`/`.razor`),
- a **markup layer** for XAML, with the C# code-behind analysed through Roslyn,
- a **pattern scanner** for SQL (comments and literals masked, then the rules that need no schema),
- **text** — `search_text`, `text_index` (FTS5), `read_file`, and any tool that does not need a syntax
  tree.

Around those, the non-.NET toolchains are wrapped rather than reimplemented: `node_*` runs the project's
own npm/pnpm/yarn, `tsc` and `eslint`; `k8s_*` runs `kubectl` and `helm`; `docker_*` runs the engine. The
wrapper reports a missing client instead of installing one.

The full-text index deliberately accepts many extensions, so XAML, SQL, YAML, JS/TS and F# files are
*findable*; they are not *understood*. That distinction is the whole of this section.

| Language / platform | What works today | What is missing |
|---|---|---|
| **C#** | The full surface: semantic navigation, references, callers, refactorings, style, inspections, complexity, profiling, decompilation | Only the general limits in [reference/limits.md](public/reference/limits.md): dynamic dispatch is not modelled, and a static call graph needs `find_reflection_usage` / `framework_entry_points` to cover reflection and framework conventions (both shipped); behaviour is measured by `verify_change` when the suite covers the change |
| **Visual Basic** | The **symbol index** (declarations, uses by kind, hierarchy, outline, metacode, unused); **metacode** (translate, compare, index trees and complexity, generate); the scoped tools over both languages (`structural_search` with VB patterns, `format_code`, `optimize_usings` for `Imports`, `code_cleanup`); **inspections** through the packs that ship VB analyzers (.NET analyzers, the VMC0001 house rule) and `inspection_baseline`; **analysis** (`find_dead_code`, `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns`, `complexity_report`, `get_complexity_hotspots`); **refactorings** (`apply_rename`, `encapsulate_field`, `add_params`/`remove_params`); `simplify_vb_code` (15 ids, 34 refused with a reason); `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `run_code_analysis`; text search/index; Git and file tools | The refactorings that are C# syntax or C# idiom (primary constructors, records, file-scoped namespaces, expression bodies, `using` static) — they refuse with that language reason; the C#-only inspection packs report that they do not cover a VB project; `code_cleanup`'s member reordering; `get_metrics`, `analyze_complexity`, `get_cohesion_metrics`, `find_duplicate_code`, `fix_complexity`, `generate_docs`, `implement_members` |
| **F#** | `.fs`/`.fsproj` are in the full-text index; `build_project` returns the F# compiler's own diagnostics (FS####) because it parses the MSBuild log; `run_tests` runs F# tests; Git and file tools | No semantic model: F# is not a Roslyn language, so no symbols, references, refactorings, inspections or complexity |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | `xaml_pages` (inventory), `analyze_xaml` (XAML0001–XAML0009), `xaml_navigate` (names, resources, styles, handlers); the C# code-behind is fully analysed | No compiled-XAML validation: binding paths, styles and tag helpers are not proved; no refactoring across markup and C#; no designer or preview |
| **JavaScript / TypeScript / JSX / TSX** | `node_info`, `node_install`, `node_script` (the project's npm/pnpm/yarn); `node_typecheck` (tsc → `TSxxxx` diagnostics with file:line), `node_lint` (eslint → findings with rule and position), `node_outdated`, `node_audit`; the files are in the full-text index | No parser or module graph for the JS/TS itself: no symbols, references, rename or refactoring; no bundler or component analysis; no browser preview |
| **HTML / CSS / SCSS** | `.html`, `.htm`, `.css`, `.scss` are in the full-text index | No parser, no accessibility analysis, no CSS-in-JS or CSS-isolation analysis, no formatting or linting |
| **SQL** | `sql_files` (per-file statements, verbs and tables) and `analyze_sql` (SQL0001–SQL0010: lexical, correctness, safety, security and performance rules) | No dialect parser, no schema model, no validation against a database, no execution, no query plan, no formatting, and no language-injection analysis for SQL embedded in C# strings |
| **EF Core** | The C# model — `DbContext`, entities, configurations — is analysed like any C#; `ef_migrations` runs the project's own `dotnet ef` (list, add, script, remove, update, info, dbcontext-list) | No model-vs-migrations/snapshot diff, no generated-SQL inspection, no seeding help |
| **Kubernetes / Helm** | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs` read the cluster; `k8s_apply`, `k8s_delete`, `k8s_rollout` write with `confirm=true`; `k8s_helm` covers releases; Docker has a local container surface | No manifest or chart schema validation, no kustomize, no exec or port-forward; YAML is otherwise full-text only |
| **Razor / Blazor** | Page inventory, 17 markup/accessibility rules (RZ2001–RZ2017), 11 security rules (RZ1001–RZ1011), verified edits, Razor→metacode | No Razor compilation: `@model` members, tag helpers, component parameters, cascading values, `@bind` types, CSS isolation and JS interop are unchecked |
| **Python / C++** | `translate_python_file` (CPython's `ast` dumper) and `translate_cpp_file` (`clang -ast-dump=json`) lower a file to canonical pseudocode, with origin lines and a diagnostic per unmodelled construct; `dumpJson` accepts a dump produced elsewhere | No type checker, no symbol navigation, no editing — the readers model syntax for comparison, not a language service |
| **Java, Go, Rust** | `MetacodeLanguage` has labels | No frontend, no analysis, no navigation, no editing |

### 1.1 C#

C# is the only language with a complete tool surface, and it is where every other documentation page
applies. Its own limits are not language gaps and live in
[Limits → Analysis](public/reference/limits.md#analysis): reflection-only code looks dead, source
generators are outside the model, generated files may or may not appear, and anything that crosses the
solution boundary is metadata rather than analysed source.

### 1.2 Visual Basic

VB is a language a .NET solution can *contain*, and the tools that are language-neutral at the semantic
level now read VB syntax. **Closed 2026-10-31**: what used to be a style layer is a language surface.

What exists:

- `simplify_vb_code`: **15 of the ids in the VB column** of Microsoft's code-style table (simplify name
  and member access, remove `Me.`, unnecessary cast, predefined type, `Is Nothing`, `If(a, b)`, `?.`,
  parentheses, compound assignment, boolean equality, conditional simplification, `IsNot`, `ByVal`).
  The other **34 VB ids are returned with a written reason**; none is silently ignored.
- **Symbol index** (`reference_index`): `VisualBasicSymbolIndexFrontend` records VB declarations, every
  use with its kind (a compound assignment is a read-write, `Inherits`/`Implements` are inherit/implement
  edges) and one metacode tree per declaration, so `find`, `describe`, `refs`, `callers`, `callees`,
  `hierarchy`, `outline`, `metacode` and `unused` answer for `.vb` documents. Keys are language-neutral
  (`Method:global::Ns.C.M(ref global::System.String)`): a C# caller of a VB method lands on the same row
  as the VB declaration, and the index is dropped once when the key format changes.
- **Metacode**: `metacode_translate`, `metacode_compare`, `metacode_index` (trees and language-neutral
  complexity) and `metacode_generate` read `.vb`.
- **Structural search and replace** (`structural_search`, `language=auto|cs|vb`): VB patterns with
  `$holes$` — `Twice($argument$)`, `$x$ Is Nothing`, `Return $value$` — parsed inside a wrapper (VB has
  no free-standing "parse a statement") and matched by the same engine as C#; a replacement is spliced
  back with the match's own trivia.
- **The scoped document tools**: `format_code` (Roslyn's formatter over VB), `optimize_usings` (`Imports`
  statements, including aliases and `Imports System.Math` type imports), `code_cleanup` (style, usings,
  regions and format; member reordering stays C#-only).
- **Inspections**: `inspect_code` and `inspection_baseline` run each pack's analyzers for the project's
  language. The .NET analyzers ship a `vb` set and the house rule VMC0001 has a VB implementation;
  Roslynator, StyleCop, AsyncFixer, Meziantou and the ReSharper-shaped pack are C# analyzers and say so
  as a warning instead of returning a silent zero.
- **Analysis**: `find_dead_code` (statements after a jump, constant `If`, dead `Select Case` arms),
  `find_code_smells` (`Async Sub`, empty `Catch`, `Await` in `SyncLock`, long/deep/wide methods),
  `find_async_anti_patterns` (`.Result`/`.Wait()`/`.GetResult()`, `Await` in loops, `Task.Run` around a
  sync lambda, missing `ConfigureAwait`), `find_performance_anti_patterns`, `complexity_report` and
  `get_complexity_hotspots` (McCabe, cognitive, Halstead, MI over VB blocks).
- **Refactorings** that are not C# syntax: `apply_rename` (Roslyn's renamer, across documents),
  `encapsulate_field` (private field plus `Get`/`Set` property, references rewritten), `add_params` /
  `remove_params` (`ParamArray`, with the compiler gate on the call sites).
- The language-neutral runtime tools: `build_project`, `run_project`, `run_tests`,
  `get_test_coverage_map`, `get_diagnostics` (compilation diagnostics include VB projects).
- `run_code_analysis` collects **the project's own analyzer references** for the project's language.
- Text: `text_index`, `search_text`, `read_file`, `read_files`.
- Roslyn navigation (`find_symbol`, `find_references`, `find_callers`, `find_implementations`,
  `find_derived_types`, `get_type_members`, `read_symbol`, `get_symbol_info`) resolves VB declarations in
  a mixed solution.

What is still missing:

| Capability | VB today |
|---|---|
| Refactorings | ⚠️ rename, `encapsulate_field` and the `ParamArray` pair apply; the rest of the catalogue is C# syntax or C# idiom (primary constructors, records, file-scoped namespaces, expression bodies, `using` static, top-level statements) and refuses with that reason |
| `simplify_code` | ⚠️ VB has its own `simplify_vb_code` with 15 ids; the other 34 are returned with a reason |
| `structural_search`, `format_code`, `optimize_usings` | ✅ both languages; `code_cleanup` covers VB for style/usings/regions/format |
| `inspect_code` packs, `inspection_baseline` | ⚠️ the packs that ship VB analyzers run (netanalyzers, house); the C#-only packs report that they do not cover the project, and there is no VB fixer surface |
| Symbol index (`reference_index`) | ✅ |
| Dead code, code smells, async/performance patterns, complexity report | ✅ |
| Metacode translate/generate | ✅ `VisualBasicFrontend` lowers modules, classes, structures, interfaces, enums, properties, methods, events and the statement/expression vocabulary; `With … End With`, `ReDim`, `On Error` and `RaiseEvent` stay `RAW[vb]` with a diagnostic, and `metacode_generate` emits C# from a `.vb` file |
| Everything else | ❌ `get_metrics`, `analyze_complexity`, `get_cohesion_metrics`, `find_duplicate_code`, `fix_complexity`, `reduce_complexity`, `generate_docs`, `implement_members` and the context actions walk C# syntax |

Workaround: for those last tools, C# only — a VB project keeps the symbol index, the inspections, the
analysis and the refactorings listed above.


### 1.3 F#

F# is a first-class .NET language but not a Roslyn language: `MSBuildWorkspace` loads C# and VB projects,
so the semantic engine has no F# model to give. What it does have is the compiler: `build_project` runs
`dotnet build` and parses the MSBuild log, which is where the F# compiler's own `FS####` diagnostics
appear; `run_tests` runs an F# test project, and `search_text`/`text_index`/`read_file` read the source.

What is absent, and stays absent as a product boundary rather than as a pending item: symbols, references,
rename, refactorings, inspections and complexity. Those need FSharp.Compiler.Service — a second compiler
platform embedded next to Roslyn — and this product is a Roslyn server. The boundary is in section 1's
table and repeated in section 13 so a comparison with an IDE stays honest.

### 1.4 XAML — WPF, WinUI 3, MAUI, Avalonia, UWP

**Implemented** (2026-10-30): a markup layer that reads `.xaml`/`.axaml` from disk and resolves the
C# through Roslyn — `xaml_pages` (inventory), `analyze_xaml` (XAML0001–XAML0009: duplicate
`x:Name`/`x:Key`, missing or unresolvable `x:Class`, an event handler with no method, a resource key
nothing defines, a `DataTemplate` without `DataType`, a name colliding with a member, invalid XML),
`xaml_navigate` (`x:Name` ↔ generated field, resource key ↔ uses, style `BasedOn` chain, handler ↔
method).

Still missing, because these need the XAML to be *compiled*, which the layer does not do:

- **binding-path validation** against the DataContext type, `@bind` type compatibility, tag-helper
  resolution, required attributes;
- **CSS isolation** (`.razor.css`) and JS interop analysis;
- **XAML refactorings**: rename an element or resource across markup and C#, extract a style,
  promote a literal to a resource;
- **design-time surface**: no XAML preview, no designer, no visual tree, no binding inspector;
- **project awareness**: no distinction between the WPF, WinUI, MAUI and Avalonia dialects; no
  MAUI multi-targeting awareness; no XAML formatting.

### 1.5 JavaScript, TypeScript, HTML and CSS

**Implemented** (2026-10-31): the toolchain, not the language. `node_*` detects the package manager the
repository actually uses (`packageManager`, then `pnpm-lock.yaml` / `yarn.lock` / `package-lock.json`,
then npm), installs dependencies (`ci` / `--frozen-lockfile` on request), runs a `package.json` script, and
turns the two diagnostic tools' output into data: `node_typecheck` parses `tsc --pretty false` into `TSxxxx`
diagnostics with `file(line,col)`, counts and the raw output when nothing parsed; `node_lint` parses
`eslint --format json` into findings with rule id, severity and position plus the rules that fire most;
`node_outdated` and `node_audit` normalise the npm, pnpm and yarn 1 shapes. It never downloads: `tsc` and
`eslint` are resolved in `node_modules/.bin`, and a missing one is reported with the command that installs it.

Still missing, because it needs a JavaScript/TypeScript frontend:

- no parser or module graph, so no `find_symbol`, references, callers, rename or refactoring *in* the JS/TS;
- no JSX/TSX component analysis, props/state checks or bundler configuration understanding;
- no CSS/HTML analysis (those files remain full-text);
- no browser preview or DOM inspection — the `signal_r_*` tools exercise a SignalR hub at runtime, which
  is not JavaScript analysis.

### 1.6 SQL, databases and EF Core

**Implemented** (2026-10-31): `sql_files` inventories the `.sql` files (lines, statements, verbs, tables
named) and `analyze_sql` applies SQL0001–SQL0010. The scanner masks `--`/`/* */` comments, single-quoted
literals (with `''` escapes), dollar-quoted bodies and bracket-quoted identifiers before it matches, so a
keyword inside a comment or a string does not fire; findings carry `file:line:column`, the source line and
a fix.

What it deliberately does not do, because it is a pattern scanner and not a dialect parser: no schema
object model, no validation against a database, no formatting, no execution, no query-plan capture, no
schema comparison, and no language-injection analysis for SQL embedded in C# strings.

EF Core is visible only from the C# side: `DbContext`, `DbSet<>`, entity classes and configuration
classes are ordinary C# and get the full C# tool surface. **Implemented** (2026-10-30): `ef_migrations`
runs the project's own `dotnet ef` (list, add, script, remove, update, info, dbcontext-list) and
returns the command, exit code, stdout and stderr as structured data.

Still missing:

- a diff between the model and the migrations/snapshot, or between two migrations;
- inspection of the SQL a query or migration will generate;
- seeding and data operations.

What remains outside the tool: the database itself. `ef_migrations` covers the workflow and a migration
file is C# reviewed with the normal tools; running the resulting SQL and inspecting the schema stays with
the database's own client.

### 1.7 Containers and orchestration

Docker has a real surface: `docker_ps`, `docker_run`, `docker_control`, `docker_rm`, `docker_images`,
`docker_pull`, `docker_image_rm`, `docker_logs`, `docker_inspect`, `docker_stats`, `docker_networks`,
`docker_volumes`, `docker_prune`, `docker_compose` and `docker_status` — 15 tools covering the local
container lifecycle and compose stacks.

Missing:

- Dockerfile analysis or linting, image layer/size analysis, build-cache inspection;
- registry operations (login, tag, push), image vulnerability scanning;
- Kubernetes: `k8s_*` wraps kubectl (status, context, get as rows, describe, logs, apply/delete/rollout
  with `confirm`) and `k8s_helm` wraps releases; still missing are manifest/chart schema validation,
  kustomize, and exec/port-forward;
- YAML schema validation for compose/k8s manifests (YAML is only full-text indexed).

### 1.8 Razor and Blazor

What exists is real and documented in [razor.md](razor.md): page inventory, 17 markup/accessibility rules
(RZ2001–RZ2017), 11 security rules with CWE ids (RZ1001–RZ1011), verified edits through `razor_edit`, and
a Razor→metacode frontend.

What is missing is everything that needs Razor to be *compiled*:

- `@model` property and type validation, `@using` resolution, tag-helper resolution and required
  attributes;
- component parameters, cascading values, `@bind` type compatibility, event callback signatures;
- CSS isolation (`.razor.css`) mapping and unused-selector analysis;
- JS interop (`IJSRuntime` calls against the actual JS module) and static-asset analysis;
- Razor refactorings: rename a model property across page and code-behind, extract a component, move
  markup into a partial;
- layout/section/route validation beyond what the heuristic scanner does.

`.cshtml.cs`/`.razor.cs` code-behind files are C# and get the full C# surface, which is the practical
workaround: put logic in the code-behind and it is analysed.

### 1.9 Project and platform coverage

| Project kind | State |
|---|---|
| ASP.NET Core (MVC, Razor Pages, Minimal APIs, Web API) | ✅ C# at source level; Razor markup by scanner. No runtime request tracing (profiling is process-level) |
| WinForms | ⚠️ C# is analysed; `.Designer.cs` is generated code and several analyses skip it; no designer |
| WPF / WinUI 3 / MAUI / Avalonia / UWP | ⚠️ C# analysed; XAML is text (section 1.4) |
| gRPC | ⚠️ generated C# analysed; `.proto` files are not parsed |
| SignalR | ⚠️ hubs are C#; no hub-contract analysis. Runtime client tools (`signal_r_*`) exist to exercise a hub |
| Unity / Godot / MAUI workloads | ⚠️ whatever MSBuild can restore is loaded; no specialist project support |
| Legacy non-SDK projects | ⚠️ loaded through MSBuild; `update_framework` deliberately leaves `net4x`/`netstandard` alone |
| Solution/project creation | ❌ no `dotnet new`-style templates; `aspire` is the only scaffolder, `add_projects_to_solution` adds existing projects |

---

## 2. Debugger

**Implemented** (2026-10-30): a Debug Adapter Protocol client over
[netcoredbg](https://github.com/Samsung/netcoredbg), installed on demand (`debug_install`), with 16
tools: `debug_status`, `debug_install`, `debug_launch` (breakpoints in the same call, waits for the
first stop), `debug_attach`, `debug_breakpoints` (conditional and hit-count, verified vs unverified),
`debug_continue`, `debug_step` (over/into/out), `debug_pause`, `debug_threads`, `debug_stack`
(file:line per frame), `debug_variables` (Locals/Arguments/Autos, one level of expansion),
`debug_evaluate` (watch), `debug_exceptions`, `debug_output`, `debug_terminate`, `debug_sessions`.
Sessions survive across tool calls, so a stop that happened between two calls is still there.

Still missing, and the reasons:

- **edit-and-continue and hot reload**: netcoredbg does not implement them; the .NET hot-reload agent
  is a separate protocol. Neither is exposed here.
- **data breakpoints and exception settings per type**: the adapter's filter set is `all` /
  `user-unhandled`; a per-exception-type configuration is not exposed.
- **the debugger windows** (breakpoints list, call stack, modules, parallel stacks): UI, the client's
  job — the data behind them is in `debug_sessions`, `debug_stack` and `debug_threads`.

---

## 3. Inspection administration and background analysis

Findings are strong (`inspect_code` exposes ~800 ids from five packs, `apply_code_fix` runs the
analyzer's own fix, `inspection_baseline` gives a CI gate), and the administration around them is now
covered as well:

| Capability | State |
|---|---|
| Severity per rule, per scope (solution/project/folder/file) | ✅ `configure_inspection` writes `dotnet_diagnostic.<id>.severity` at solution, project or single-file scope |
| Suppress a finding (`SuppressMessage`, `#pragma`, `.editorconfig` severity) | ✅ `suppress_finding` (pragma around the member or the attribute), compiler-gated; `configure_inspection` for the severity |
| `.DotSettings` layers, shared team settings | ⚠️ `read_dot_settings` reads `.DotSettings`/`.DotSettings.user` (severities, disabled ids, custom dictionary words). The severities are ReSharper inspection ids and are reported, not applied to Roslyn diagnostics — there is no id map between the two catalogs |
| Custom inspections authored in the product | ✅ `custom_inspections`: rules declared in `.visualMCP/inspections.json` (banned symbols/namespaces/attributes, identifier patterns, string-literal and comment patterns) with id, severity and message; they have no code fix |
| ReSharper's proprietary set (~2500 ids) | ⚠️ `resharper_inspection_map` is the exact state: **8 ids are implemented natively under ReSharper's names** in the opt-in `rider` inspection pack (`ConvertToConstant_Local`, `LoopVariableIsNeverChangedInsideLoop`, `MemberCanBeMadeStatic_Global`, `RedundantArgument`, `EmptyConstructor`, `RedundantIfElseBlock`, `ConditionIsAlwaysTrueOrFalse`, `RedundantBaseConstructorCall`); ~30 more map to an existing capability (`simplify_code` IDE id, a Roslyn pack, a dedicated tool, a context action); 10 well-known ids have none. The remaining ~2500 are closed-source analyzers over JetBrains' PSI — each a reimplementation, not a registration — so they are not ported |
| Solution-wide background analysis / daemon | ✅ `background_analysis` starts a warm loop that re-runs the packs when the loaded solution changes; `analysis_findings` serves the cached findings between calls (by file, project, severity or id) |
| Formatting settings management, `.editorconfig` generation | ✅ `configure_formatting` authors `csharp_*`/`dotnet_*`/`indent_*`/`end_of_line`/`charset`/`file_header_template`; severities with `configure_inspection` |
| Naming rules (`IDE1006`) application | ✅ `naming_rules` reads `dotnet_naming_rule`/`symbols`/`style` from the `.editorconfig` chain, lists the violations with the expected name, and with `apply=true` renames them through `apply_rename` (recorded and revertible). Locals and parameters are listed only |
| Spelling inspection | ✅ `find_spelling_issues`: flags words one edit away from a word the solution or `.visualMCP/spelling.dic` + the `.DotSettings` words already use — no bundled English dictionary, so the evidence is the repository's own vocabulary |
| Quick-fix menu / preview UI | 💀 the client shows the diff; the server returns it |

The practical consequence: a team can run the inspections, gate a change with `inspection_baseline`,
tune one rule and suppress one finding where the compiler reads it, add rules without building an
analyzer, apply the naming policy, and keep a warm analysis running while it edits. What remains is the
ReSharper proprietary catalogue itself: eight of its ids are implemented here, the map says which
capability answers ~30 more, and the rest would be a reimplementation of a closed-source product rather
than a gap a tool can close.

---

## 4. Test workflow

| Capability | State |
|---|---|
| Run the suite, structured pass/fail, duration, failure details | ✅ `run_tests` |
| Run one project, forward raw `dotnet test` arguments | ✅ |
| Single test / rerun failed | ✅ `run_test` (testName/className/raw filter, TRX result) and `rerun_failed` (the recorded failures of the last run) |
| Per-class/per-method line coverage | ✅ `get_test_coverage_map` (coverlet) |
| Branch coverage | ✅ reported from Cobertura (`branch-rate`, per class and overall) |
| Debug a test / run to a failing assertion | ✅ `debug_test`: VSTest starts with `VSTEST_HOST_DEBUG`, the test host pid is read from its output, the DAP adapter attaches and the breakpoints are set before resuming. `debug_test(sessionId: …)` reads what the host printed (the test results) |
| Continuous testing (run on save) | ✅ `continuous_testing`: action=start watches the source files' write times and runs the tests after a debounce; status is cheap to poll, run-now runs once |
| "Which tests cover this symbol?" | ✅ `tests_covering_symbol` (direct references from test methods; indirect coverage is not traced) |
| Which tests a change affects | ✅ `test_impact_analysis` (git diff → the symbols the changed files declare → the tests that reference them) |
| Test scaffolding (generate a test class for a type) | ✅ `generate_tests` (xUnit, one `[Fact]` per public method, compiler-gated) |
| Parameterized/data-driven test discovery | ✅ `list_tests` reads the test attributes from the source and lists the inline `[InlineData]`/`[TestCase]`/`[DataRow]` rows with the framework; `MemberData`/`TestCaseSource`/`DynamicData` are named as dynamic |
| Mutation testing (Stryker) | ✅ `run_mutation_tests` runs Stryker.NET and returns the mutation score, the killed/survived/timed-out/no-coverage counts, the worst files and the surviving mutants with file and line |
| Test impact analysis (run only affected tests) | ✅ `run_affected_tests` composes `test_impact_analysis` with the run: the affected tests are executed with the composed filter, or the answer is `no-affected-tests` |
| Flaky detection | ✅ `find_flaky_tests` runs the selection several times and reports the tests whose outcome changed, apart from the ones that never passed |
| Test explorer UI, grouping | 💀 the client renders the list `list_tests` returns |

---

## 5. Decompiled code as a workspace

**Implemented** (2026-10-30): `assembly_workspace` decompiles an assembly (ILSpy) into a Roslyn
`AdhocWorkspace` — one document per type, the platform references and the assembly's own folder —
keyed on the module version id, and then navigates it: `load`, `list`, `search` (exact → prefix →
CamelHumps → contains), `source`, `references` (Roslyn's symbol finder inside the decompiled set),
`outline`, `unload`. Go-to-definition and find-usages on code without sources are no longer
impossible.

Still missing:

- a C#↔IL toggle or an integrated IL view (`disassemble` decodes the method bodies to IL, and
  `decompile_to_metacode` gives the pseudocode; the views just are not linked);
- rename inside the decompiled set (it would need a mapping back to IL, and an edit has no source to
  write);
- debugging the decompiled code (it needs the debugger plus a source-mapping step);
- an assembly explorer UI (`outline` is the data behind it).

---

## 6. Context actions, templates and generation

ReSharper's C# layer ships **1176 actions** — 869 quick fixes attached to its ~2500 inspections and 307
context actions (the census is [resharper-context-actions.md](resharper-context-actions.md)). VisualMCP
has:

- **97 C# style ids** (`simplify_code`; the other 29 ids are delegated, not applicable to C#, or not a
  rewrite — each named with its category) and **15 VB ids**;
- **~800 inspection ids**; a fix exists only where the analyzer ships a `CodeFixProvider`
  (`apply_code_fix`);
- **36 context-action ids** through `context_action`: `import_type`, the argument guards
  (`check_param_null`, `check_all_params_null`, `check_string_not_null_or_empty`,
  `check_collection_not_empty`, `check_variable_null`, `check_dictionary_key`, `check_enum_defined`,
  `check_index_range`), the wrap actions (`wrap_in_try_catch`, `wrap_in_try_finally`, `wrap_in_if`,
  `wrap_in_using`, `wrap_in_region`), the string re-encodings (`string_to_raw`, `string_to_verbatim`,
  `string_to_regular`), the statement family (`add_braces`, `foreach_to_for`, `for_to_foreach`,
  `invert_loop`, `if_to_switch`, `concat_to_interpolated`, `interpolated_to_concat`),
  `digit_separators` / `remove_argument_name`, and the second batch (`merge_sequential_checks`,
  `split_and_in_if`, `join_null_check_with_usage`, `use_var`, `use_explicit_type`, `split_declaration`,
  `remove_braces`, `migrate_to_nullable`, `remove_digit_separators`, `use_configure_await_false`);
- **templates** (`templates`): 18 live, 10 postfix (`.if`, `.null`, `.not`, `.var`, `.await`,
  `.foreach`, `.forr`, `.ret`, `.toList`, `.switch`) and 8 file templates (class, interface, record,
  struct, enum, static class, abstract class, xUnit test), plus the team's own in
  `.visualMCP/templates.json`; a file template creates the document behind the compiler gate;
- generators: `implement_members`, `create_from_usage`, `generate_docs`, `generate_members`,
  `implement_inpc`, `generate_tests`.

What that does not cover:

| Missing family | Examples |
|---|---|
| Import/completion actions | `import_type` adds the using for a type already named on the line, and `remove_argument_name` makes a named argument positional; still missing symbol completion, static-member import, qualify/unqualify |
| Wrap actions | try/catch, try/finally, if, using and #region are covered; still missing quotes and the "Surround with" template family |
| Guard actions | the common null/empty guards, the index/range guard, the dictionary-key guard and the enum-defined guard are covered; still missing span-index and contract annotations |
| Literal/string actions | raw/verbatim/regular re-encoding, both directions of interpolation↔concatenation and digit separators are covered; still missing escape/unescape of individual characters, split/join and the StringBuilder rewrites |
| Statement actions | `foreach`↔`for`, add-braces, `if`-chain→`switch`, `invert_loop`, merge/split conditions and remove-braces are covered; still missing statement ↔ expression |
| Type actions | a DTO from JSON (`generate_dto_from_json`) and a builder (`generate_builder`) are covered; still missing a DTO from XML and a comparer |
| Test actions | `generate_tests` scaffolds a class; still missing "add a test for this member" and "run the covering test" |
| Editor policy | naming rules, and `use_var`/`use_explicit_type` as explicit actions (`IDE0007`/`IDE0008` are deliberately not style rewrites), are covered; still missing code-style enforcement on save and licence-header insertion (`IDE0073` needs the repository's template) |
| Regex and injections | regex completion/validation, and language injections (SQL/HTML/CSS/JSON embedded in a C# string is just text) |

The split matters when comparing numbers with ReSharper: **87 style ids ≠ 87 context actions**, and
**~800 inspection ids ≠ 800 fixes**. Four families exist (style rewrites, analyzer code fixes, the
hand-written context-action ids, and the eight mechanical compiler fixes in `fix_diagnostics`). The
compiler family is the one that had **no** automatic fix until 2026-10-30; it now covers the shapes that
need no judgement — CS0168/CS0219 unused local, CS0169 unused field, CS0067 unused event, CS0105
duplicated using, CS0108/CS0114 hidden member, CS0109 redundant `new` — each accepted only when the
project compiles no worse, and every other `CS####` still has no fix here.

---

## 7. Refactoring gaps

All ~51 refactorings of Rider's "main set" are implemented; the list itself is in
[resharper-codemaid-coverage.md](resharper-codemaid-coverage.md#refactorings).

Closed 2026-10-31, each compiler-gated and applied to the declaration plus every call site or reference in
one operation:

- **Transform parameters** (`transform_parameters`) — the Change Signature operations that are more than a
  reorder: `wrap` replaces a consecutive block of parameters with a nested parameter object (the body reads
  `args.Property`), `make-optional` and `make-required` add and remove defaults, `change-type` retypes them,
  and `replace-with-property` turns a parameter into a property of the containing type with the assignment
  inserted before each call on a receiver that is safe to name twice.
- **Make non-static** (`make_non_static`) — the inverse of `make_static`: call sites are routed through an
  instance already in scope (`this`, or the nearest local, parameter or field of the type) and the operation
  is refused, listing the sites, when one has no instance. Nothing is fabricated.
- **Move type into another type** (`move_type_to_nested_scope`) — the declaration becomes nested in the
  target type, every reference outside the two declarations becomes `Target.Source`, and a source file left
  with no declarations is deleted. Gated on the whole solution, because the type's metadata name changed.
- **Generify** (`generify`) — a concrete type used in a method or type declaration becomes a type parameter.
  Positions where a type parameter cannot stand (static member access, attribute, `nameof`, `const`) keep the
  concrete type; a method type parameter that cannot be inferred gets the concrete type as an explicit type
  argument at every call site, and a type gets its arguments at every reference. Constraints are passed in,
  because only the caller knows what the body needs.

What is left of that family is not a refactoring: the interactive dialog and the UI in
[section 11](#11-ui-bound-features--not-applicable-by-design).

---

## 8. Architecture, diagrams and exploration

The numbers and the diagram data are both here; what remains is the rendering, which an MCP server
returns as text for the client (section 11):

- **Graphs as artifacts**: `export_dependency_graph` at project, namespace and **type** level
  (`analyze_dependencies`, `analyze_circular_namespace_dependencies`, `analyze_namespace_coupling` and
  `check_layer_violations` remain the numbers); `call_graph` (incoming/outgoing, a depth, Mermaid/DOT);
  `sequence_diagram` (static call order); `control_flow_graph` (Roslyn basic blocks, branch conditions and
  try regions).
- **Value tracking**: `track_value` lists the definitions (writes, including the declaration) and the uses
  (reads) of a symbol under a position, each with file:line, member and snippet.
- **Structure view / file structure**: `reference_index action=outline` (a file's declarations) and
  `get_type_members`; breadcrumbs are the client's. Recent files/edits: `edit_history` and `command_log`.
- **Code vision**: the data is `reference_index action=refs`/`callers`; inlay hints are editor UI.
- **Localization analysis**: `localization_report` — unused `.resx` keys, missing translations per culture,
  extra keys, and user-facing literals as candidates. Spelling: `find_spelling_issues`.
- **Explorers**: an assembly explorer is `assembly_workspace`/`inspect_assembly`, the NuGet tool window is
  `list_nuget_packages`/`check_nuget_updates`, the solution explorer is `solution_map`/`read_tree`.

Implemented 2026-10-30: the **endpoints view** (`find_endpoints`), the **TODO explorer**
(`find_todos`), **go to file** by fuzzy/CamelHumps name (`find_file`), **stack-trace analysis**
(`analyze_stack_trace`) and the codebase map (`solution_map`).

`plan_feature_change` produces an ordered text plan with impact counts; it is not a clickable impact
graph — `impact` counts plus `export_dependency_graph` are the data behind one.

---

## 9. Version control and collaboration

Git has a complete local surface (`git_*`: status, diff, log, blame, stage, commit, amend, branch, tag,
stash, reset, merge, rebase, cherry-pick, revert, squash, remote, sync, show, run — plus the structured
`git_worktree`, `git_bisect` and `git_submodule` since 2026-10-31) and the hosting side covers all four
providers a .NET team uses: GitHub through the authenticated `gh` CLI (`gh_repo`, `gh_pr`, `gh_issue`,
`gh_release`, `gh_actions`, `gh_secret`, `gh_branch_protection`) and GitLab, Azure DevOps and Bitbucket
Cloud through `git_host` (merge/pull requests, their comments and merges, issues/work items and
pipelines/builds, with credentials read from `GITLAB_TOKEN`, `AZURE_DEVOPS_EXT_PAT` and `BITBUCKET_*`).
The provider, host, project and repository are parsed from the git remote.

Conflicts have a **model**, not just a report: `git_conflicts` parses the `<<<<<<<` blocks of every
unmerged file into hunks with all three sides (`ours`, `theirs`, and the `|||||||` base when
`merge.conflictStyle=diff3` is on), rewrites a chosen strategy over one hunk or the whole file
(`ours`, `theirs`, `both`, `base`, or `custom` text), and stages the file once no marker remains. The
merge/rebase/cherry-pick tools report the same conflict set and the operation the repository is in.

**Interactive rebase** is data: `git_rebase action=plan` takes the base revision and a JSON todo list
of `pick`/`reword`/`squash`/`fixup`/`drop` steps with their messages, hands it to git through
`GIT_SEQUENCE_EDITOR`, and never opens an editor. **Patches** are named shelves on disk: `git_patch`
creates from the working tree, the index or any revision/range, lists and shows them, applies with
`--check`, `--3way` or `--reverse`, and deletes them — the shareable counterpart of the anonymous
`git_stash`.

The **review workflow** is assembled from data the server already has: `gh_pr action=review` posts a
GitHub review with line-anchored comments (`path`/`line`/`side`), `action=review-comments` lists them,
`action=checks` returns the check runs on the PR head, `gh_branch_protection` inspects protection rules,
required status checks and repository rulesets, and `code_review` builds the review brief for any diff:
changed files by kind, the risk signals a reviewer looks for first, the new TODO markers with their
line, the worst methods of each changed C# file, and (opt-in) the inspection findings inside them.

**Real-time collaborative editing** (Rider's Code With Me) is not a gap to close: it is a shared
transport and an editor surface, not a tool a local stdio server can offer. It belongs with the
not-applicable-by-design rows in [section 11](#11-ui-bound-features--not-applicable-by-design).

---

## 10. Build, run and packaging

Build and run are covered (`build_project`, `run_project`, `run_tests`, `run_benchmarks`,
`quick_benchmark`, `run_plan`, `add_ci_workflow`). **Packaging is covered since 2026-10-30**
(`dotnet_publish` with RID/self-contained/single-file, `nuget_pack`, `nuget_push`) together with
**project references** (`add_project_reference`, `remove_project_reference`). What remains:

- MSIX, ClickOnce, WiX/installer authoring;
- publish profiles and launch settings editing;
- no project-property editing surface beyond `update_framework`, `implicit_usings` and the package tools;
  the csproj is otherwise text;
- **NuGet gaps**: `search_nuget_packages` finds a package id in the nuget.org gallery (id, version,
  description, downloads, authors, tags); still missing is package-source and credential management;
- **build diagnostics gaps**: no MSBuild binary-log (`.binlog`) analysis, no evaluated-property dump, no
  build graph or per-target timings — `build_project` returns compiler diagnostics; `extract_build_log_errors`
  parses a text log;
- no run configurations (per-project env vars/args stored and reused — `run_project` takes arguments per
  call); a `.http` request file runs through `run_http_file`, and `.http` files edited elsewhere are text;
- no deployment targets (IIS, Azure, containers, Kubernetes);
- **no standalone command-line tool** for a pipeline (the equivalents of ReSharper's `inspectcode`,
  `dupfinder`, `cleanupcode`): the MCP server must be driven by an MCP client, so CI needs a harness;
- no feature-level migration: `update_framework` moves the TFM and `update_solution` moves packages, but
  there is no "enable nullable across the solution", "convert to file-scoped namespaces", "adopt primary
  constructors" campaign tool; `change_nullability` and `simplify_code` work per declaration/document.

---

## 11. UI-bound features — not applicable by design

These are not gaps to close. An MCP server returns data; the client renders it. They are listed so the
comparison with an IDE is honest.

| IDE feature | Why it is not here |
|---|---|
| Code editor, syntax highlighting, folding | The client's job |
| Completion, parameter info, quick-info popup | Editor UI; the underlying symbol data is available through `get_symbol_info`/`get_type_members` |
| Quick-fix bulb and menu | The server returns the fix and its diff; the client shows it |
| Tool windows (Solution Explorer, NuGet, Test Explorer, Assembly Explorer) | UI; the data is exposed as tools |
| Settings UI, `.editorconfig`/`.DotSettings` editors | Files on disk; the server consumes them |
| On-save cleanup, code vision, inlay hints | Editor lifecycle |
| XAML/WinForms designers, previews | UI + platform-specific parsers (the parsers are the real gap, section 1.4) |
| Debugger UI (breakpoint list, call stack window) | The debugger *engine* is the gap (section 2); the window is the client's job |
| Browser preview, database consoles | Out of scope for this product |
| AI Assistant, full-line completion, AI commit messages | The client's model is the assistant; the server supplies the tools it calls |
| Remote development, cloud workspaces, Code With Me | The server is a local stdio process over local paths |

---

## 12. Beyond ReSharper: SonarQube, CodeScene and the profiling IDEs

The [capability map](../CAPABILITY_MAP.md#gap-prioritari-vs-sonarqubecodescene) tracks these separately;
they are repeated here because they are also "what is missing" for a .NET team:

| Gap | What it would need |
|---|---|
| Taint analysis (SQL injection/XSS across the data flow) | A data-flow engine; `find_security_issues` is pattern-based |
| Mutation testing | ✅ `run_mutation_tests` (Stryker.NET) returns the mutation score and the survivors; the runner itself is an external tool the user installs |
| Quality trend between commits | Storing findings per revision and diffing them; `inspection_baseline` only freezes one state |
| Database profiling, async/event-source viewers, instrumentation profiling | The four Visual Studio profiling tools not covered (see [profiling.md](profiling.md)) |

The **Git-aware churn hotspot** row closed on 2026-10-30: `git_churn_hotspots` joins
`git log --numstat` over a bounded window with the Roslyn complexity ranking and scores each file as
commits × the cyclomatic complexity of its worst method. It is static (static model + history), not a
runtime measurement.

---

## 13. What to do today

This section used to be a workaround table. **Closed 2026-10-31**: every row is now either a tool in this
build or a boundary stated with its reason, and the rows whose feature already existed are deleted. The
table below is that boundary — the first column is the question, the second says which call answers it or
where the answer stops and why.

| You need | The tool — or where the boundary is |
|---|---|
| Read, search or edit a file no frontend reads (YAML, JSON, config, Markdown, C/C++, Python, an OData/XML manifest) | `read_file`/`read_files`, `search_text`, `text_index`. The text layer **is** the design for a file with no syntax model; it is not a missing parser. Python and C/C++ can also be lowered with `translate_python_file`/`translate_cpp_file` for comparison, not for analysis |
| HTML/CSS/SCSS diagnostics | Not here: no parser for either. The files are findable and readable. |
| XAML diagnostics and navigation | `xaml_pages`, `analyze_xaml` (XAML0001–XAML0009), `xaml_navigate`. Bindings, styles and tag helpers are not compiled, so they are not proved |
| SQL diagnostics | `sql_files` and `analyze_sql` (SQL0001–SQL0010). A pattern scanner, not a dialect parser: no schema, no execution, no query plan (§1.6) |
| JS/TS diagnostics and build | `node_info`/`node_install`/`node_script` for the toolchain; `node_typecheck` (tsc → `TSxxxx`) and `node_lint` (eslint → findings) for the diagnostics; `node_outdated`/`node_audit` for the dependency health. The JS/TS source itself still has no semantic model (§1.5) |
| EF Core migrations | `ef_migrations` (list/add/script/remove/update/info/dbcontext-list). Model-vs-snapshot diff and generated-SQL inspection are not covered (§1.6) |
| Kubernetes and Helm | `k8s_status`/`k8s_context`/`k8s_get`/`k8s_describe`/`k8s_logs` read; `k8s_apply`/`k8s_delete`/`k8s_rollout` write with `confirm=true` (apply defaults to a server-side dry run); `k8s_helm` covers releases. No manifest/chart schema validation, no kustomize, no exec/port-forward (§1.7) |
| VB refactoring | `apply_rename`, `encapsulate_field`, `add_params`/`remove_params` and `simplify_vb_code` (15 ids) apply; the refactorings that are C# syntax or C# idiom refuse with that reason, and the C#-only analyses are listed in §1.2 |
| F# analysis | `build_project` returns the F# compiler's diagnostics (FS####), `run_tests` runs F# tests, `search_text`/`text_index` read the files. No symbols, references or refactorings: F# is not a Roslyn language, and a second compiler platform is out of the product's scope (§1.3) |
| Debug a process | `debug_launch`/`debug_attach` with breakpoints, stepping, `debug_stack`, `debug_variables`, `debug_evaluate` (16 `debug_*` tools); `profile_stacks`/`profile_dump`/`profile_slowdown` remain the post-mortem path (§2) |
| Single test | `run_test` (name, class or raw filter, TRX result) and `rerun_failed`; `debug_test` for the failing one (§4) |
| Inspection suppression and severity | `suppress_finding` (a `#pragma` around the member or a `SuppressMessage` attribute, compiler-gated) and `configure_inspection` (severity per scope); `inspection_baseline` gates CI (§3) |
| Decompiled symbol navigation | `assembly_workspace` (load, search, source, references, outline over a Roslyn workspace) and `inspect_assembly`/`find_assembly_api`. Rename and debugging inside the decompiled code are not possible: there is no source to write (§5) |
| Taint analysis across a data flow | Not here: `find_security_issues` is pattern-based (§12) |
| Quality trend between commits | Not here: `inspection_baseline` freezes one state (§12) |
| Real-time collaborative editing | A shared transport and an editor surface, not a local stdio tool (§11) |

---

## 14. How this inventory is maintained

- The ✅ side lives in [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md). This page is the
  ❌/⚠️ side and the reasons behind them.
- When a gap closes, delete its row here and add the tool to the coverage table; do not leave a "was
  missing" note.
- When a new tool ships, check whether it removes a row here — the two pages are meant to sum to the same
  product, not to be two independent claims.
- "Not applicable" rows stay: they are the boundary of the product, not a backlog.
