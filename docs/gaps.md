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
| ❌ | A real gap. The feature exists in ReSharper/Rider and could be built here. | Implement it, or accept the workaround in section 13 |
| ⚠️ | Partial. Something exists, and the row says exactly what is missing. | Extend the existing tool |
| 💀 | Not applicable by design. The feature belongs to an editor UI, not to a headless MCP server. | Nothing — the client supplies the UI |

Last updated: 2026-10-31 (sixth revision: six context actions close the literal/statement/guard rows —
`interpolated_to_concat`, `digit_separators`, `remove_argument_name`, `check_dictionary_key`,
`check_enum_defined`, `invert_loop`, 20 → 26 action ids — `translate_python_file` / `translate_cpp_file`
make the two opt-in metacode readers reachable, `run_mutation_tests` runs Stryker.NET, and `run_http_file`
executes a request file: 318 → 322). The
style counts (87 C# ids, 15 VB ids) are read from the catalogues; the inspection figure is the documented
~800 from five packs.

---

## At a glance

| Area | State | Priority | Why this priority |
|---|---|:---:|---|
| [Language and platform scope](#1-language-and-platform-scope) | ❌ | High if your solution uses them | XAML and EF Core are covered now; a JS/SQL/F# codebase still gets almost nothing beyond text search |
| [Debugger](#2-debugger) | ✅ | — | Implemented: 16 `debug_*` tools over netcoredbg (DAP). The engine is here; the window is the client's ([section 11](#11-ui-bound-features--not-applicable-by-design)) |
| [Inspection administration](#3-inspection-administration-and-background-analysis) | ⚠️ | Medium | Severity in `.editorconfig` and compiler-gated suppressions exist; `.DotSettings`, custom inspections and a background daemon do not |
| [Test workflow](#4-test-workflow) | ⚠️ | Medium | One test, re-run the failures, which tests reach a symbol, a scaffold class, branch coverage and mutation testing exist; debugging a test and continuous testing do not |
| [Decompiled code as a workspace](#5-decompiled-code-as-a-workspace) | ✅ | — | `assembly_workspace` decompiles into a Roslyn workspace: search, source, references, outline |
| [Context actions and templates](#6-context-actions-templates-and-generation) | ⚠️ | Medium | 26 action ids now (guards incl. dictionary/enum/index, wrap, braces, loops incl. invert, if→switch, both interpolation directions, digit separators, argument name); the long tail is still the long tail |
| [Refactoring gaps](#7-refactoring-gaps) | ⚠️ | Low | 41 of Rider's main set are there (`remove_params` and bidirectional call hierarchy arrived); the remaining names are listed |
| [Diagrams and exploration](#8-architecture-diagrams-and-exploration) | ❌ | Low | Text/graph export exists; visual exploration does not |
| [Version control and collaboration](#9-version-control-and-collaboration) | ⚠️ | Low | Git (including worktrees, bisect and submodules) and GitHub are covered; the rest of the VCS ecosystem is not |
| [Build, run, packaging](#10-build-run-and-packaging) | ⚠️ | Low | Build/test/run/publish exist; package search is covered; installers and deployment targets do not |
| [UI-bound features](#11-ui-bound-features--not-applicable-by-design) | 💀 | — | Deliberately out of scope for an MCP server |

---

## 1. Language and platform scope

The semantic engine is **Roslyn over C#**. Everything else is one of three things:

- a dedicated **Razor/Blazor markup scanner** (heuristics over `.cshtml`/`.razor`),
- a **Visual Basic style layer** (`simplify_vb_code` only),
- **text** — `search_text`, `text_index` (FTS5), `read_file`, and any tool that does not need a syntax
  tree.

The full-text index deliberately accepts many extensions, so XAML, SQL, YAML, JS/TS and F# files are
*findable*; they are not *understood*. That distinction is the whole of this section.

| Language / platform | What works today | What is missing |
|---|---|---|
| **C#** | The full surface: semantic navigation, references, callers, refactorings, style, inspections, complexity, profiling, decompilation | Only the general limits in [reference/limits.md](public/reference/limits.md): reflection/source-generated code is outside the call graph, dynamic dispatch is not modelled, everything is static |
| **Visual Basic** | `simplify_vb_code` (15 ids, 34 refused with a reason); `build_project`, `run_tests`, `run_project`, `get_diagnostics`; the project's own analyzers through `run_code_analysis`; text search/index; Git and file tools | Everything dedicated: no VB refactorings, no VB inspections from the built-in packs, no VB formatter, no VB structural search, no VB style beyond the 15 ids, no VB metacode frontend, no VB symbol index |
| **F#** | `.fs`/`.fsproj` are in the full-text index; `build_project`/`run_tests` work if the SDK does; Git and file tools | No semantic model at all — F# is not a Roslyn language, so no symbols, references, refactorings or inspections |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | `.xaml`/`.axaml` are in the full-text index; the C# code-behind is fully analysed | No XAML parser, no markup diagnostics, no binding/resource/name analysis, no XAML navigation, no XAML refactorings, no designer or preview |
| **JavaScript / TypeScript / JSX / TSX** | `.js`, `.mjs`, `.ts`, `.tsx`, `.jsx` are in the full-text index | No parser, no type checker, no module graph, no ESLint/Prettier, no npm/pnpm/yarn, no package.json auditing, no bundler or browser preview |
| **HTML / CSS / SCSS** | `.html`, `.htm`, `.css`, `.scss` are in the full-text index | No parser, no accessibility analysis, no CSS-in-JS or CSS-isolation analysis, no formatting or linting |
| **SQL** | `.sql` is in the full-text index; SQL text inside C# strings is visible as text | No SQL parser, no schema model, no query validation, no formatting, no execution, no query plan, no language-injection analysis |
| **EF Core** | The C# model — `DbContext`, entities, configurations — is analysed like any C#; migrations are C# files | No migrations workflow: no `dotnet ef migrations add/list/script/remove`, no model-snapshot diff, no generated-SQL inspection, no seeding help |
| **Kubernetes / Helm** | `.yml`/`.yaml` are in the full-text index; Docker has a local container surface | No kubectl/Helm/kustomize, no manifest schema validation, no cluster state, no port-forward/logs for pods |
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

VB is the second most valuable gap, because a .NET solution can *contain* it and the product does not
say so loudly enough.

What exists:

- `simplify_vb_code`: **15 of the ids in the VB column** of Microsoft's code-style table (simplify name
  and member access, remove `Me.`, unnecessary cast, predefined type, `Is Nothing`, `If(a, b)`, `?.`,
  parentheses, compound assignment, boolean equality, conditional simplification, `IsNot`, `ByVal`).
  The other **34 VB ids are returned with a written reason**; none is silently ignored.
- The language-neutral runtime tools: `build_project`, `run_project`, `run_tests`,
  `get_test_coverage_map`, `get_diagnostics` (compilation diagnostics include VB projects).
- `run_code_analysis` collects **the project's own analyzer references** for the project's language, so
  a VB project with VB analyzers gets its findings. `inspect_code`, by contrast, loads the built-in
  packs' **C# analyzers only** and is not a VB inspection surface.
- Text: `text_index`, `search_text`, `read_file`, `read_files`.
- Roslyn navigation (`find_symbol`, `find_references`, `find_callers`, `find_implementations`,
  `find_derived_types`, `get_type_members`, `read_symbol`, `get_symbol_info`) is built on
  `SymbolFinder`/the semantic model and can resolve VB declarations in a mixed solution. It is not a
  documented or tested VB surface, and every tool that walks C# syntax skips VB documents.

What is missing:

| Capability | VB today |
|---|---|
| Refactorings (rename is the exception, via Roslyn) | ❌ none of the refactoring tools applies |
| `simplify_code` | ❌ C# only; VB has `simplify_vb_code` with 15 ids |
| `structural_search` | ❌ C# syntax only |
| `format_code`, `optimize_usings`, `code_cleanup` | ❌ C# only (the services filter or cast to C# syntax) |
| `inspect_code` packs, `inspection_baseline` | ❌ built-in packs are C# analyzers |
| Symbol index (`reference_index`) | ❌ C# documents only (VB is counted as `unsupported`) |
| Metacode index (`metacode_index`) | 🟡 VB is read natively and stored in `source_document`, and `build` reports it as `unsupported`: it has no frontend, so no trees. Razor has one. |
| Dead code, code smells, async/performance patterns, complexity report | ❌ the implementations walk C# syntax |
| Metacode translate/generate | ❌ no VB frontend; the tool answers that it has none |

Workaround: use `simplify_vb_code`, `run_code_analysis` with the project's own analyzers, and the text
tools; do refactoring in an IDE. If VB matters to you, this is a product decision to make explicitly,
not a limitation to discover late.

### 1.3 F#

F# files are indexed as text and can be built and tested through `dotnet` when the SDK is installed, but
there is no semantic layer: `MSBuildWorkspace` loads C# and VB projects, not F#. That means no
`find_symbol`, no references, no refactorings, no inspections, no complexity, no metacode. An F# codebase
gets file reading, regex/FTS search, Git, build, test and profiling of a running process — nothing that
understands a declaration.

Workaround: `search_text`/`text_index` over `.fs`, and external F# tooling (Ionide, Fantomas) through
`run_command`.

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

These files are searchable and nothing more:

- no parser or type checker, so no `find_symbol`, references, callers, rename or refactoring;
- no module graph (`import`/`require` resolution), so no dependency analysis across JS/TS;
- no npm/pnpm/yarn integration: `package.json` is a file, not a package graph; no audit, no outdated
  check, no lockfile analysis;
- no ESLint/Prettier/stylelint execution (you can run them through `run_command`, but the server does not
  interpret the results);
- no JSX/TSX component analysis, no props/state checks, no bundler configuration understanding;
- no browser preview or DOM inspection — the `signal_r_*` tools exercise a SignalR hub at runtime, which is
  not JavaScript analysis.

Workaround: `search_text`/`text_index` for reading, `run_command` for `npm`/`eslint`/`prettier`/`tsc`.
The files are excluded from every Roslyn-based tool, which is correct rather than accidental.

### 1.6 SQL, databases and EF Core

`.sql` files are text. There is no T-SQL/PostgreSQL/SQLite dialect parser, no schema object model, no
query validation, no formatting, no execution, no query-plan capture, no schema comparison, and no
language-injection analysis for SQL embedded in C# strings.

EF Core is visible only from the C# side: `DbContext`, `DbSet<>`, entity classes and configuration
classes are ordinary C# and get the full C# tool surface. **Implemented** (2026-10-30): `ef_migrations`
runs the project's own `dotnet ef` (list, add, script, remove, update, info, dbcontext-list) and
returns the command, exit code, stdout and stderr as structured data.

Still missing:

- a diff between the model and the migrations/snapshot, or between two migrations;
- inspection of the SQL a query or migration will generate;
- seeding and data operations.

Workaround: `ef_migrations` for the workflow, `run_command` with `dotnet ef` for anything it does not
wrap, and `sqlcmd`/`psql` for the database. A migration file is C# and can be reviewed with the normal
tools.

### 1.7 Containers and orchestration

Docker has a real surface: `docker_ps`, `docker_run`, `docker_control`, `docker_rm`, `docker_images`,
`docker_pull`, `docker_image_rm`, `docker_logs`, `docker_inspect`, `docker_stats`, `docker_networks`,
`docker_volumes`, `docker_prune`, `docker_compose` and `docker_status` — 15 tools covering the local
container lifecycle and compose stacks.

Missing:

- Dockerfile analysis or linting, image layer/size analysis, build-cache inspection;
- registry operations (login, tag, push), image vulnerability scanning;
- Kubernetes: no kubectl, no manifest or Helm chart analysis, no kustomize, no cluster state, no
  pod logs/exec/port-forward;
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
- **debug a unit test**: reachable by launching the test host assembly under `debug_launch`, but no
  first-class "run this test under the debugger" tool yet.
- **data breakpoints and exception settings per type**: the adapter's filter set is `all` /
  `user-unhandled`; a per-exception-type configuration is not exposed.
- **the debugger windows** (breakpoints list, call stack, modules, parallel stacks): UI, the client's
  job — the data behind them is in `debug_sessions`, `debug_stack` and `debug_threads`.

---

## 3. Inspection administration and background analysis

Findings are strong (`inspect_code` exposes ~800 ids from five packs, `apply_code_fix` runs the
analyzer's own fix, `inspection_baseline` gives a CI gate). Administration is the gap:

| Capability | State |
|---|---|
| Severity per rule, per scope (solution/project/folder/file) | ✅ `configure_inspection` writes `dotnet_diagnostic.<id>.severity` at solution, project or single-file scope |
| Suppress a finding (`SuppressMessage`, `#pragma`, `.editorconfig` severity) | ✅ `suppress_finding` (pragma around the member or the attribute), compiler-gated; `configure_inspection` for the severity |
| `.DotSettings` layers, shared team settings | ❌ ReSharper-specific format, not read |
| Custom inspections authored in the product | ❌ you can point `run_code_analysis` at analyzers the project already references, but there is no inspection SDK or registration |
| ReSharper's proprietary set (~2500 ids) | ⚠️ overlap only with the ~800 pack ids; the rest are absent |
| Solution-wide background analysis / daemon | ❌ every call analyses on demand; there is no warm daemon, no squiggles between calls, no incremental solution-wide pass |
| Formatting settings management, `.editorconfig` generation | ✅ `configure_formatting` authors `csharp_*`/`dotnet_*`/`indent_*`/`end_of_line`/`charset`/`file_header_template`; severities with `configure_inspection` |
| Naming rules (`IDE1006`) application | ❌ naming requires a rename; it is not a rewrite |
| Spelling inspection | ❌ |
| Quick-fix menu / preview UI | 💀 the client shows the diff; the server returns it |

The practical consequence: a team can run the inspections, gate a change with `inspection_baseline`, and
now tune one rule and suppress one finding where the compiler reads it. What remains is the
ReSharper-shaped administration: `.DotSettings`, custom inspections, and a daemon that analyses while
nobody asks.

---

## 4. Test workflow

| Capability | State |
|---|---|
| Run the suite, structured pass/fail, duration, failure details | ✅ `run_tests` |
| Run one project, forward raw `dotnet test` arguments | ✅ |
| Single test / rerun failed | ✅ `run_test` (testName/className/raw filter, TRX result) and `rerun_failed` (the recorded failures of the last run) |
| Per-class/per-method line coverage | ✅ `get_test_coverage_map` (coverlet) |
| Branch coverage | ✅ reported from Cobertura (`branch-rate`, per class and overall) |
| Debug a test / run to a failing assertion | ❌ no first-class tool: launch the test host under `debug_launch` and set a breakpoint |
| Continuous testing (run on save) | ❌ |
| "Which tests cover this symbol?" | ✅ `tests_covering_symbol` (direct references from test methods; indirect coverage is not traced) |
| Which tests a change affects | ✅ `test_impact_analysis` (git diff → the symbols the changed files declare → the tests that reference them) |
| Test scaffolding (generate a test class for a type) | ✅ `generate_tests` (xUnit, one `[Fact]` per public method, compiler-gated) |
| Parameterized/data-driven test discovery | ⚠️ what the runner reports; no test-case model |
| Mutation testing (Stryker) | ✅ `run_mutation_tests` runs Stryker.NET and returns the mutation score, the killed/survived/timed-out/no-coverage counts, the worst files and the surviving mutants with file and line |
| Test impact analysis (run only affected tests) | ⚠️ `test_impact_analysis` names the affected tests and a ready filter; running exactly them is the caller's `run_test` call |
| Test explorer UI, grouping, flaky detection | 💀 / ❌ |

What remains is the part that needs its own engine: continuous testing. Mutation testing now runs through
`run_mutation_tests` (Stryker.NET; the external runner is a tool dependency, not a bundled engine).

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

- **87 C# style ids** (`simplify_code`, with 39 refused and a reason) and **15 VB ids**;
- **~800 inspection ids**; a fix exists only where the analyzer ships a `CodeFixProvider`
  (`apply_code_fix`);
- **26 context-action ids** through `context_action`: `import_type`, the argument guards
  (`check_param_null`, `check_all_params_null`, `check_string_not_null_or_empty`,
  `check_collection_not_empty`, `check_variable_null`, `check_dictionary_key`, `check_enum_defined`,
  `check_index_range`), the wrap actions (`wrap_in_try_catch`, `wrap_in_try_finally`, `wrap_in_if`,
  `wrap_in_using`, `wrap_in_region`), the string re-encodings (`string_to_raw`, `string_to_verbatim`,
  `string_to_regular`), the statement family (`add_braces`, `foreach_to_for`, `for_to_foreach`,
  `invert_loop`, `if_to_switch`, `concat_to_interpolated`, `interpolated_to_concat`) and
  `digit_separators` / `remove_argument_name`;
- generators: `implement_members`, `create_from_usage`, `generate_docs`, `generate_members`,
  `implement_inpc`, `generate_tests`.

What that does not cover:

| Missing family | Examples |
|---|---|
| Import/completion actions | `import_type` adds the using for a type already named on the line, and `remove_argument_name` makes a named argument positional; still missing symbol completion, static-member import, qualify/unqualify |
| Wrap actions | try/catch, try/finally, if, using and #region are covered; still missing quotes and the "Surround with" template family |
| Guard actions | the common null/empty guards, the index/range guard, the dictionary-key guard and the enum-defined guard are covered; still missing span-index and contract annotations |
| Literal/string actions | raw/verbatim/regular re-encoding, both directions of interpolation↔concatenation and digit separators are covered; still missing escape/unescape of individual characters, split/join and the StringBuilder rewrites |
| Statement actions | `foreach`↔`for`, add-braces, `if`-chain→`switch` and `invert_loop` are covered; still missing statement ↔ expression |
| Type actions | a DTO from JSON (`generate_dto_from_json`) and a builder (`generate_builder`) are covered; still missing a DTO from XML and a comparer |
| Test actions | `generate_tests` scaffolds a class; still missing "add a test for this member" and "run the covering test" |
| Templates | live templates, postfix templates (`.if`, `.null`, `.var`), file templates, a template editor |
| Editor policy | naming rules, `var` preferences (`IDE0007`/`IDE0008` are deliberately not rewritten), code-style enforcement on save, licence-header insertion (`IDE0073` needs the repository's template) |
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

46 of the ~51 in Rider's "main set" are implemented. The refactorings themselves are listed in
[resharper-codemaid-coverage.md](resharper-codemaid-coverage.md#refactorings); this table names only what
is absent or partial, with the closest tool.

| Missing operation | Closest today | What is missing |
|---|---|---|
| Transform parameters | `change_signature` | Reorder/add/remove only; no "transform" (e.g. wrap a parameter in a type, replace with a property) |
| Abstract class ↔ interface | `extract_interface`, `extract_superclass` | Neither direction |
| Make non-static | `make_static` | Only the reverse direction: a static method has no instance to convert its call sites to, so this one needs a design decision, not a rewrite |
| Move type into another type | `move_type`, `move_to_folder` | Not implemented |
| Generify | — | Not implemented |

---

## 8. Architecture, diagrams and exploration

`export_dependency_graph` emits DOT or Mermaid, and `analyze_dependencies`,
`analyze_circular_namespace_dependencies`, `analyze_namespace_coupling`, `check_layer_violations` and
`get_cohesion_metrics` give the numbers. What does not exist is the visual, interactive side:

- type dependency diagram, project graph view, namespace graph view (only a text format for external
  renderers);
- sequence diagram, call graph visualisation, control-flow graph view;
- **value tracking** (where a value comes from, origin → destination);
- structure view / file structure popup, breadcrumbs, bookmarks, recent files/edits;
- code vision (references/usages above a declaration), inlay hints;
- spelling inspection, **localization analysis** (hard-coded strings, unused `.resx` entries, missing
  translations);
- assembly explorer, NuGet tool window, solution explorer.

Implemented 2026-10-30: the **endpoints view** (`find_endpoints`), the **TODO explorer**
(`find_todos`), **go to file** by fuzzy/CamelHumps name (`find_file`), **stack-trace analysis**
(`analyze_stack_trace`) and the codebase map (`solution_map`).

`plan_feature_change` produces an ordered text plan with impact counts; it is not a clickable impact
graph.

---

## 9. Version control and collaboration

Git has a complete local surface (`git_*`: status, diff, log, blame, stage, commit, amend, branch, tag,
stash, reset, merge, rebase, cherry-pick, revert, squash, remote, sync, show, run — plus the structured
`git_worktree`, `git_bisect` and `git_submodule` since 2026-10-31) and GitHub has a
structured surface (`gh_repo`, `gh_pr`, `gh_issue`, `gh_release`, `gh_actions`, `gh_secret`).

Missing:

- **local history** (per-file snapshots independent of commits) — ReSharper's Local History;
- **3-way merge assistant**: conflicts are reported by merge/rebase/cherry-pick and resolved by editing
  the files; there is no merge editor or conflict model;
- **interactive rebase** (the squash tool covers the common case; `git_run` can do the rest, without a
  structured result);
- **patch/shelve** beyond `git_stash`;
- **GitLab, Azure DevOps, Bitbucket** — only GitHub is integrated;
- **line-anchored PR review comments** (`gh_pr` posts comments; it does not build an inline review with
  comments on specific diff lines);
- code review workflow, pull-request check annotations, branch protection inspection;
- real-time collaborative editing (Rider's Code With Me) — there is no shared session, only files on disk.

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

| You need | Workaround |
|---|---|
| Read/search XAML, SQL, YAML, JS/TS, CSS, F# | `search_text` or `text_index`; `read_file`/`read_files` |
| XAML/SQL/JS diagnostics | External tool through `run_command` (XAML: the IDE; SQL: `sqlcmd`/`psql`; JS: `tsc`/ESLint) |
| EF Core migrations | `run_command` with `dotnet ef`; review migration `.cs` files with the C# tools |
| Kubernetes | `run_command` with `kubectl`/`helm`; manifests are text |
| npm/TS build | `run_command` with `npm`/`pnpm`/`yarn`/`tsc` |
| VB refactoring | `simplify_vb_code` for the 15 ids; an IDE for the rest |
| F# analysis | External F# tooling; `build_project`/`run_tests` still work |
| Debug a process | `profile_stacks`, `profile_dump`, `profile_slowdown`, `profile_threadpool` for the post-mortem; reproduce with `run_project` |
| Single test | `run_tests` with raw `dotnet test --filter` arguments, or `run_command` |
| Inspection suppression | Edit `.editorconfig`/`SuppressMessage` by hand; `inspection_baseline` for a CI gate |
| Decompiled symbol navigation | `inspect_assembly`/`find_assembly_api` and `decompile_type`; no cross-reference navigation |
| Local history | Git: commit often, or a branch/stash; `edit_history` only covers writes this server made |

---

## 14. How this inventory is maintained

- The ✅ side lives in [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md). This page is the
  ❌/⚠️ side and the reasons behind them.
- When a gap closes, delete its row here and add the tool to the coverage table; do not leave a "was
  missing" note.
- When a new tool ships, check whether it removes a row here — the two pages are meant to sum to the same
  product, not to be two independent claims.
- "Not applicable" rows stay: they are the boundary of the product, not a backlog.
