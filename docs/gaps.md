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
| ❌ | A real gap. The feature exists in ReSharper/Rider and could be built here. | Implement it; section 12 says where the remaining boundaries are |
| ⚠️ | Partial. Something exists, and the row says exactly what is missing. | Extend the existing tool |
| 💀 | Not applicable by design. The feature belongs to an editor UI, not to a headless MCP server. | Nothing — the client supplies the UI |

Last updated: 2026-11-01 — **416 tools** in the running build (`capability_report` prints the live
count). Version control and collaboration is not on this page: it is a feature section of
[resharper-codemaid-coverage.md](resharper-codemaid-coverage.md), because none of it is a gap. The
batches that closed rows here, each with the count it produced, are in
[§14 Revision log](#14-revision-log); anything older is in `git log`.

---

## At a glance

| Area | State | Priority | Why this priority |
|---|---|:---:|---|
| [Language and platform scope](#1-language-and-platform-scope) | ⚠️ | Low if your solution uses them | C#, VB, XAML, Razor, EF Core, Kubernetes and the Node toolchain are covered semantically or by a wrapper; SQL, JS/TS, HTML/CSS, YAML and F# each have a **scanned** layer with an inventory and a rule set. What none of the scanners does is resolve a symbol or type-check |
| [Debugger](#2-debugger) | ✅ | — | Implemented: 20 `debug_*` tools over netcoredbg (DAP) — line, function and data breakpoints, per-exception-type filters, value editing and restart. Edit-and-continue is the one thing the adapter exposes only over GDB/MI, so it is an engine limit ([reference/limits.md](reference/limits.md#debugger)) rather than a missing tool; the windows are the client's ([section 10](#10-ui-bound-features--not-applicable-by-design)) |
| [Inspection administration](#3-inspection-administration-and-background-analysis) | ✅ | — | Severity, suppression, custom rules, `.DotSettings` reading, naming rules, spelling and a warm analysis loop exist; only the proprietary ReSharper id set is absent |
| [Test workflow](#4-test-workflow) | ✅ | — | Running, discovery, debugging a test, continuous testing, affected tests, flaky detection and mutation testing all exist; only the explorer UI is the client's |
| [Decompiled code as a workspace](#5-decompiled-code-as-a-workspace) | ✅ | — | `assembly_workspace` decompiles into a Roslyn workspace: search, source, references, outline |
| [Context actions and templates](#6-context-actions-templates-and-generation) | ⚠️ | Low | 41 action ids and the template family (18 live, 10 postfix, 8 file) exist, including LINQ both ways, StringBuilder, pattern merging and the C# 14 extension block; what remains is the proprietary tail whose condition is a type-system fact rather than a shape |
| [Refactoring gaps](#7-refactoring-gaps) | ✅ | — | All ~51 of Rider's main set are implemented, including `transform_parameters`, `make_non_static`, `move_type_to_nested_scope` and `generify` (2026-10-31) |
| [Diagrams and exploration](#8-architecture-diagrams-and-exploration) | ✅ | — | Graph artifacts (project/namespace/type, call, sequence, control flow), value tracking, localization and the explorers exist as data; drawing them is the client's |
| [Build, run, packaging](#9-build-run-and-packaging) | ⚠️ | Low | Build/test/run/publish and the six build-settings tools (evaluated MSBuild, binlog, run configurations, launch/publish profiles, package sources) exist; installer authoring, deployment targets, a standalone CLI and solution-wide style campaigns do not |
| [UI-bound features](#10-ui-bound-features--not-applicable-by-design) | 💀 | — | Deliberately out of scope for an MCP server |

---

## 1. Language and platform scope

The semantic engine is **Roslyn**, and it reads the two languages a .NET solution can be written in:
**C#** and **Visual Basic**. Everything else is one of five things:

- a dedicated **Razor/Blazor markup scanner** (heuristics over `.cshtml`/`.razor`),
- a **markup layer** for XAML, with the C# code-behind analysed through Roslyn,
- a **pattern scanner** per file kind — SQL, JavaScript/TypeScript, HTML/CSS, YAML and F# — where
  comments and literals are masked first and the rules that need no type information are applied
  (`analyze_sql`, `analyze_js`, `analyze_markup`, `analyze_yaml`, `analyze_fsharp`),
- an **inventory** per file kind (`sql_files`, `js_files`, `markup_files`, `yaml_files`,
  `fsharp_files`): what the file declares, which modules it imports, the ids of a page, the
  `kind`/`apiVersion` of a manifest,
- **text** — `search_text`, `text_index` (FTS5), `read_file`, and any tool that does not need a syntax
  tree.

Around those, the non-.NET toolchains are wrapped rather than reimplemented: `node_*` runs the project's
own npm/pnpm/yarn, `tsc` and `eslint`; `k8s_*` runs `kubectl` and `helm`; `docker_*` runs the engine. The
wrapper reports a missing client instead of installing one.

The full-text index deliberately accepts many extensions, so every file a repository carries is
*findable*. A scanned language is the next step: its rules are read from masked source and stated one
by one. What no scanner does is resolve a symbol or type-check an expression — that is the distinction
this section keeps.

| Language / platform | What works today | What is missing |
|---|---|---|
| **C#** | The full surface: semantic navigation, references, callers, refactorings, style, inspections, complexity, profiling, decompilation | Only the general limits in [reference/limits.md](reference/limits.md): dynamic dispatch is not modelled, and a static call graph needs `find_reflection_usage` / `framework_entry_points` to cover reflection and framework conventions (both shipped); behaviour is measured by `verify_change` when the suite covers the change |
| **Visual Basic** | The **symbol index** (declarations, uses by kind, hierarchy, outline, metacode, unused); **metacode** (translate, compare, index trees and complexity, generate); the scoped tools over both languages (`structural_search` with VB patterns, `format_code`, `optimize_usings` for `Imports`, `code_cleanup` including member reordering); **inspections** through the packs that ship VB analyzers (.NET analyzers, the VMC0001 house rule) and `inspection_baseline`; **analysis** (`find_dead_code`, `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns`, `complexity_report`, `get_complexity_hotspots`, `get_metrics`, `analyze_complexity`, `get_cohesion_metrics`, `find_duplicate_code`); **refactorings and generation** (`apply_rename`, `encapsulate_field`, `add_params`/`remove_params`, `generate_docs`, `implement_members`, `reduce_complexity`/`fix_complexity`); `simplify_vb_code` (15 ids, 34 refused with a reason); `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `run_code_analysis`; text search/index; Git and file tools | The refactorings that are C# syntax or C# idiom (primary constructors, records, file-scoped namespaces, expression bodies, `using` static) — they refuse with that language reason; the C#-only inspection packs report that they do not cover a VB project; the context actions are C#-shaped and have no VB matcher; `reduce_complexity` reports extract-method as a hand edit |
| **F#** | `fsharp_files` — the declaration inventory F# has no symbol index for (modules, types, top-level functions, opens); `analyze_fsharp` — FS0001–FS0010 over masked source (tabs, `Option.get`, partial list access, `Unchecked.defaultof`/`unbox`, null comparison, `with _ ->`, `let mutable`, `printf`, `failwith`, a duplicated `open`); `build_project` returns the F# compiler's own diagnostics (FS####); `run_tests` runs F# tests | No semantic model: F# is not a Roslyn language, so no symbols, references, refactorings, inspections or complexity. The scanner resolves nothing |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | `xaml_pages` (inventory + the project's dialect and target frameworks), `analyze_xaml` (XAML0001–XAML0015, binding paths validated against the data type), `xaml_bindings` (the binding inspector), `xaml_tree` (the element tree), `xaml_navigate`; verified edits `xaml_rename`, `xaml_extract_style`, `xaml_promote_resource`, `format_xaml`; the C# code-behind is fully analysed | No rendered preview or designer, and no compiled XAML: a binding whose data type the markup does not name is `not-checked` rather than guessed, and a key from a referenced assembly's theme dictionary is invisible |
| **JavaScript / TypeScript / JSX / TSX** | `node_info`, `node_install`, `node_script` (the project's npm/pnpm/yarn); `node_typecheck` (tsc → `TSxxxx` diagnostics with file:line), `node_lint` (eslint → findings with rule and position), `node_outdated`, `node_audit`; `js_files` (imports, exports, functions, classes, JSX, the relative imports resolved into a module graph with its import cycles) and `analyze_js` (JS0001–JS0017 over masked source; a relative specifier is resolved against the file system, a package specifier is not) | The relative specifiers do form a module graph (`js_files` reports the edges and the cycles), but the JS/TS itself has no parser or type model: no symbols, references, rename or refactoring; no bundler or component analysis; no browser preview. The scanner proves the shape of a line, not a binding |
| **HTML / CSS / SCSS** | `markup_files` (elements, distinct classes, selectors, ids, duplicates) and `analyze_markup`: HTML0001–HTML0015 (alt, lang, viewport, duplicate id, label binding, nested form, deprecated element, inline handler, `target="_blank"`, empty href, `button` type, control name, `http://` subresource, tag balance, a class no scanned stylesheet defines) and CSS0001–CSS0009 (`!important`, ID selector, empty rule, `@import`, duplicate selector, unbalanced braces, `javascript:` URL, `expression()`, a selector that matches no element in the scanned pages), with the CSS rules and selectors also read from every `<style>` block and the safety rules checked on an inline `style` attribute | A structural scanner, not a renderer. A selector resolves against the page index — compound parts with tag, classes, id, attribute conditions and the structural pseudo-classes `:root`/`:first-child`/`:last-child`/`:only-child`/`:empty`, joined by descendant, child, adjacent-sibling and general-sibling combinators — on the nesting the tags write. A selector whose meaning only a browser can decide (`:hover`, `:checked`, `:nth-child()`, `:has()`) is indexed but never reported; specificity and the cascade are not resolved, and a CDN stylesheet or a style built in JavaScript (CSS-in-JS) is outside the scan; no CSS-isolation analysis, no formatting |
| **YAML** | `yaml_files` (documents, top-level keys, and `kind`/`apiVersion`/`metadata.name` for a manifest) and `analyze_yaml`: YAML0001–YAML0008 (tab indentation, duplicate key, trailing whitespace, YAML 1.1 booleans, a colon glued to its key, a value that parses as a nested mapping, unbalanced flow collection, an alias with no anchor) and YAML0009–YAML0012 (kind without apiVersion or the reverse, a metadata block with no name, a container with no image, two containers with the same name) | No schema validation (Kubernetes, Helm or CI); port-forward is absent and kustomize is an offline render, not a schema check. An indentation-aware line scan, not a YAML parser |
| **SQL** | `sql_files` (per-file statements, verbs and tables) and `analyze_sql` (SQL0001–SQL0010: lexical, correctness, safety, security and performance rules); SQL built in C# is followed by `trace_data_flow` (a request value reaching `FromSqlRaw`/`ExecuteSqlRaw`/Dapper/`CommandText`) | No dialect parser, no schema model, no validation against a database, no execution, no query plan, no formatting |
| **EF Core** | The C# model — `DbContext`, entities, configurations — is analysed like any C#; `ef_migrations` runs the project's own `dotnet ef` (list, add, script, diff, remove, update, info, dbcontext-list, pending-model-changes) | Model drift is detected by `pending-model-changes`, `script` returns the migration SQL and `diff` the SQL between two migrations; no field-level comparison of the model itself against the snapshot, no inspection of the SQL a single query will generate, no seeding help |
| **Kubernetes / Helm** | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs` read the cluster; `k8s_kustomize` renders an overlay offline; `k8s_exec` runs one confirmed command inside a pod; `k8s_apply`, `k8s_delete`, `k8s_rollout` write with `confirm=true`; `k8s_helm` covers releases; `yaml_files`/`analyze_yaml` read a manifest's identity and its structural mistakes | No manifest or chart schema validation, no port-forward |
| **Razor / Blazor** | Page inventory, 17 markup/accessibility rules (RZ2001–RZ2017), 11 security rules (RZ1001–RZ1011), verified edits, Razor→metacode | No Razor compilation: `@model` members, tag helpers, component parameters, cascading values, `@bind` types, CSS isolation and JS interop are unchecked |
| **Python / C++** | `translate_python_file` (CPython's `ast` dumper) and `translate_cpp_file` (`clang -ast-dump=json`) lower a file to canonical pseudocode, with origin lines and a diagnostic per unmodelled construct; `dumpJson` accepts a dump produced elsewhere | No type checker, no symbol navigation, no editing — the readers model syntax for comparison, not a language service |
| **Java, Go, Rust** | `MetacodeLanguage` has labels | No frontend, no analysis, no navigation, no editing |

### 1.1 C#

C# is the only language with a complete tool surface, and it is where every other documentation page
applies. Its own limits are not language gaps and live in the general list,
[Limits → Static analysis](reference/limits.md#static-analysis): reflection-only code looks dead,
the trees a source generator emits are not documents (their text is returned by
`list_generated_sources`), generated files may or may not appear, and anything that crosses the
solution boundary is metadata rather than analysed source.

### 1.2 Visual Basic

VB is a language a .NET solution can *contain*, and the tools that are language-neutral at the semantic
level now read VB syntax. **Closed 2026-10-31**, in three batches: the language surface (symbol index,
metacode, analyses, metrics, generation), the two C#-shaped surfaces (context actions and code fixes),
and the style and refactoring tail — the IDE ids VB can express and the refactorings that edit a
declaration rather than C# syntax.

What exists:

- `simplify_vb_code`: **41 of the 49 ids in the VB column** of Microsoft's code-style table. The
  simplification, null-handling, cast, conditional, LINQ, layout and member rules are all there —
  simplify name and member access, remove `Me.`, unnecessary cast, predefined type, `Is Nothing`,
  `If(a, b)`, `?.`, parentheses, compound assignment, boolean equality, conditional simplification,
  `IsNot`, `ByVal`, the `Me.` qualifier (IDE0009, option-driven), `If`/`Else` folded to `If(c, a, b)`
  for a return and for an assignment (IDE0045/IDE0046), a `Select Case` completed over its enum
  (IDE0010), object initializers (IDE0017), `Dim x As New C` (IDE0140), tuple element names (IDE0033),
  `GetType(T).Name` → `NameOf(T)` (IDE0082), `NameOf` for a `paramName` literal (IDE0280),
  interpolation simplification (IDE0071), `Where(...).Any()` → `Any(...)` and `Where(TypeOf …).Cast(Of T)()`
  → `OfType(Of T)()` (IDE0120/IDE0121), `HashCode.Combine` (IDE0070), modifier order (IDE0036),
  accessibility (IDE0040), `ReadOnly` on a field only its constructor writes (IDE0044), an unused private
  member removed (IDE0051) and an unread private field removed with its stores (IDE0052), unreachable code
  (IDE0035), a dead store (IDE0059), an unused parameter removed with its argument at every call site
  (IDE0060), a trivial `Get`/`Set` pair folded into an auto-property (IDE0032), and the layout rules
  — multiple blank lines, a blank line after a block, a file header (IDE2000/IDE2003/IDE0073).
  The other **8 ids are returned with a written reason**, one per id; none is silently ignored. Five of
  them are constructs VB does not have (no collection expression, no inferred anonymous member name, no
  discard, no `??=`, no throw expression), one is a refactoring that changes the shape a caller sees
  (IDE0050), one is an audit over the compiler's diagnostics rather than a local shape (IDE0079), and one
  is a project-layout decision (IDE0130).
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
  regions, format and **member reordering**, which orders a class, structure or module by access, kind
  and name the same way it orders a C# type).
- **Inspections**: `inspect_code` and `inspection_baseline` run each pack's analyzers for the project's
  language. The .NET analyzers ship a `vb` set and the house rule VMC0001 has a VB implementation;
  Roslynator, StyleCop, AsyncFixer, Meziantou and the ReSharper-shaped pack are C# analyzers and say so
  as a warning instead of returning a silent zero.
- **Code fixes for VB** (closed 2026-10-31): `apply_code_fix` selects the analyzers **and the fixers** by
  the document's language. The .NET analyzers package ships 100+ Visual Basic `CodeFixProvider`s in its
  `vb` assembly (`Basic…Fixer`); the service offers those to a `.vb` document instead of the C# twins and
  tries each fixer that declares the id until one registers a fix. A pack with no analyzer for the
  project's language answers with that sentence.
- **Context actions** (closed 2026-10-31): `context_action` routes a `.vb` document to
  `VbContextActionService`, which implements every id of the C# catalogue that has a VB spelling:
  `import_type` (`Imports`), the argument guards (`check_param_null`, `check_all_params_null`,
  `check_string_not_null_or_empty`, `check_collection_not_empty`, `check_variable_null`,
  `check_dictionary_key`, `check_enum_defined`, `check_index_range`), the wrapping family
  (`wrap_in_try_catch`, `wrap_in_try_finally`, `wrap_in_if`, `wrap_in_using`, `wrap_in_region`), the
  conversions (`concat_to_interpolated`/`interpolated_to_concat`, `digit_separators`/
  `remove_digit_separators`, `remove_argument_name`, `use_var`/`use_explicit_type`,
  `use_configure_await_false`, `split_declaration`, `concat_to_stringbuilder`), the block shapes
  (`merge_sequential_checks`/`split_and_in_if`, `join_null_check_with_usage`, `if_to_switch` — a
  `Select Case`), both LINQ directions (`linq_query_to_method`/`linq_method_to_query`) and the loop
  conversions (`foreach_to_for`/`for_to_foreach`, `invert_loop`) — **33 ids**, each a text edit over VB
  syntax under the same compiler gate, preview and edit history. The C#-only ids (raw/verbatim strings,
  braces, `#nullable`, pattern combinators, the C# 14 extension block) are refused with a written reason.
- **Analysis**: `find_dead_code` (statements after a jump, constant `If`, dead `Select Case` arms),
  `find_code_smells` (`Async Sub`, empty `Catch`, `Await` in `SyncLock`, long/deep/wide methods),
  `find_async_anti_patterns` (`.Result`/`.Wait()`/`.GetResult()`, `Await` in loops, `Task.Run` around a
  sync lambda, missing `ConfigureAwait`), `find_performance_anti_patterns`, `complexity_report` and
  `get_complexity_hotspots` (McCabe, cognitive, Halstead, MI over VB blocks).
- **Metrics and pattern analysis** (closed 2026-10-31): `get_metrics` ranks VB methods through the same
  unit list `complexity_report` uses; `get_cohesion_metrics` reads VB classes, structures and modules
  (the member's fields are found in its enclosing block, because VB declares a method on its statement
  and puts the body in the block that follows); `find_duplicate_code` indexes VB method, constructor,
  operator and accessor bodies with a per-language token normaliser; `analyze_complexity` has a VB walk
  over `For … Next`/`For Each`, `While`, `Do … Loop`, `Select Case`, the extension-method LINQ chain and
  the `&` concatenation, with the same growth algebra, recursion classification and allocation rules the
  C# walk uses.
- **Refactorings** that are not C# syntax: `apply_rename` (Roslyn's renamer, across documents),
  `encapsulate_field` (private field plus `Get`/`Set` property, references rewritten), `add_params` /
  `remove_params` (`ParamArray`, with the compiler gate on the call sites), `change_type` (the type name
  is parsed by the VB factory, so `IEnumerable(Of Integer)` is a type name there and is not one in C#),
  `use_base_type` (the base is derived from the symbol, not typed), `type_to_partial` (every declaration
  of the type, in every file, marked `Partial` — VB rejects a mix) and `safe_delete` (a symbol with no
  reference is removed from the type block that owns it). Each runs under the same gate as its C#
  counterpart: the projection is compiled and refused when it adds an error.
- **Generation and reduction** (closed 2026-10-31): `generate_docs` writes `'''` XML documentation
  comments with the symbol-derived summary, `<param>`, `<typeparam>` and `<returns>` entries;
  `implement_members` generates `Sub`/`Function`/`Property`/`Event` stubs carrying the
  `Implements I.Member` clause VB requires, and its gate is the type's missing-member count rather than
  a diagnostic id (messages are localized); `reduce_complexity` and `fix_complexity` name a
  three-operator condition in a private `Function … As Boolean` and flatten a tail `If` of a `Sub` into
  a guard clause — extracting a block into a method is reported as a hand edit, with that reason, because
  VB has no standalone block to move.
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
| Refactorings | ⚠️ rename, `encapsulate_field`, the `ParamArray` pair, `change_signature` (the declaration and every plain call site, parentheses optional, `Optional` emitted for an added default), `change_type`, `use_base_type`, `type_to_partial`, `safe_delete`, `extract_interface` (an `Interface` plus the `Implements` clause VB requires on the type *and* on each member), `extract_method`, `move_type`, `move_to_folder` (the folder derived from the `Namespace` block), `property_to_auto` and `function_to_property` apply; what still has no VB path is the declaration-shape tail — `extract_class`/`extract_superclass`, the inline family, the move family beyond `move_type`/`move_to_folder`, and the indexer conversions |
| `simplify_code` | ✅ `simplify_vb_code` rewrites 41 of the 49 VB ids; the other 8 are returned with a reason, one per id, and five of them are constructs VB does not have |
| `structural_search`, `format_code`, `optimize_usings` | ✅ both languages; `code_cleanup` covers VB for style/usings/regions/format/reorder |
| `inspect_code` packs, `inspection_baseline` | ✅ the packs that ship VB analyzers run (netanalyzers, house) and `apply_code_fix` runs their VB `CodeFixProvider`s; the C#-only packs report that they do not cover the project |
| Context actions (`context_action`) | ✅ a `.vb` document routes to the VB engine: 33 ids with a VB spelling are implemented, the C#-only ones are refused with the reason |
| Symbol index (`reference_index`) | ✅ |
| Dead code, code smells, async/performance patterns, complexity report | ✅ |
| Metrics, cohesion, clones, Big-O (`get_metrics`, `get_cohesion_metrics`, `find_duplicate_code`, `analyze_complexity`) | ✅ |
| Generation and reduction (`generate_docs`, `implement_members`, `reduce_complexity`, `fix_complexity`) | ✅ except the extract-method move, which is reported as a hand edit |
| Metacode translate/generate | ✅ `VisualBasicFrontend` lowers modules, classes, structures, interfaces, enums, properties, methods, events and the statement/expression vocabulary; `With … End With`, `ReDim`, `On Error` and `RaiseEvent` stay `RAW[vb]` with a diagnostic, and `metacode_generate` emits C# from a `.vb` file |

Workaround: what is still C#-shaped is the refactoring catalogue's declaration-shape tail (the extract
family beyond method and interface, the inline family, the move family beyond the type and its folder,
the indexer conversions); a VB project keeps the symbol index, the inspections and their fixes, the
analysis, the metrics, the generation tools, the context actions, the style ids and the fifteen
refactorings listed above.


### 1.3 F#

F# is a first-class .NET language but not a Roslyn language: `MSBuildWorkspace` loads C# and VB projects,
so the semantic engine has no F# model to give. What it does have is the compiler: `build_project` runs
`dotnet build` and parses the MSBuild log, which is where the F# compiler's own `FS####` diagnostics
appear; `run_tests` runs an F# test project, and `search_text`/`text_index`/`read_file` read the source.

What is absent, and stays absent as a product boundary rather than as a pending item: symbols, references,
rename, refactorings, inspections and complexity. Those need FSharp.Compiler.Service — a second compiler
platform embedded next to Roslyn — and this product is a Roslyn server. The boundary is in section 1's
table and repeated in section 12 so a comparison with an IDE stays honest.

### 1.4 XAML — WPF, WinUI 3, MAUI, Avalonia, UWP

**Implemented** (2026-10-31): a markup layer that reads `.xaml`/`.axaml` from disk and resolves the C#
through Roslyn, with the project's own dialect as the thing that decides which rules apply.

- `xaml_pages` — the inventory: kind (window/page/userControl/maui/resourceDictionary/application),
  the `x:Class` code-behind resolved to its `file:line`, the **dialect of each project** with the
  signals it was decided on, and the target frameworks a MAUI project multi-targets.
- `analyze_xaml` — XAML0001–XAML0015. Markup: duplicate `x:Name`/`x:Key`, missing or unresolvable
  `x:Class`, an event handler with no method, a resource key nothing defines, a `DataTemplate` without
  its type, a name colliding with a member, invalid XML. Bindings, resolved through Roslyn: **a binding
  path whose segment does not exist on its data type**, a `Setter` without `Property`, a `Setter`
  property the style's `TargetType` does not declare, **`x:Bind` in a dialect that does not compile
  it**, an `ElementName`/`x:Reference`/`Storyboard.TargetName` that names no element, a prefixed or
  dotted `TargetType`/`DataType` that does not resolve.
- `xaml_bindings` — the binding inspector: every `{Binding}`/`{x:Bind}`/`{CompiledBinding}`/
  `{TemplateBinding}` with its target, path, mode and source; **the data type the path is checked
  against and where that type came from** (`x:DataType`, a `DataTemplate`'s `DataType`, a `DataContext`
  the code-behind assigns, the page type for `x:Bind`, the `ControlTemplate`'s `TargetType` for a
  `TemplateBinding`); the path walked member by member; and a status of `valid` / `missing-member` /
  `not-checked` with the reason.
- `xaml_tree` — the element tree the file declares (element, `x:Name`, line, attributes, children):
  **the static half of a visual tree**, as data and as indented text.
- `xaml_navigate` — `x:Name` ↔ generated field, resource key ↔ uses, style `BasedOn` chain, handler ↔
  method.
- Edits, verified: **`xaml_rename`** (an `x:Name` across markup and the code-behind's generated field;
  a resource key across the project's files and the code-behind's string literals),
  **`xaml_extract_style`** (attributes → a keyed `Style` + `Style="{StaticResource …}"`),
  **`xaml_promote_resource`** (a literal → a typed resource in the dialect's syntax),
  **`format_xaml`** (indentation of tag and attribute lines only). Each is previewed by default,
  refuses a result that would not parse as XML or would duplicate a key, and records the write in the
  edit history so `revert_edit` undoes it.
- **Project awareness**: the dialect is read from `UseWPF`, `UseMaui`, `UseWinUI`, the
  `Microsoft.WindowsAppSDK` / `Avalonia` packages or the UWP target platform, together with the
  `Directory.Build.props` chain, and reported with the signals; MAUI multi-targeting is reported as the
  frameworks the project file declares.

What remains, and why:

- **a rendered preview or a designer** — the visual tree laid out, the styles applied, a binding
  inspector against a live object. That needs a XAML rendering host, which a Roslyn server is not;
  `xaml_tree` and `xaml_bindings` are the static half of the same two answers.
- **bindings whose data type is not in the markup** — a `DataContext` produced by a data template
  selector, a DI container at runtime or assigned in another assembly's code is reported as
  `not-checked` rather than guessed at.
- **compiled XAML** — a rule that would need the generated `InitializeComponent`, a merged
  dictionary's runtime contents or a type coming from a referenced assembly's theme dictionary cannot
  answer here; XAML0005 says so in its own message.
- XAML written by a source generator (emitted at build time, no file on disk) is not scanned.

The Razor-only items this section used to list (`@bind` type compatibility, tag-helper resolution,
`.razor.css` isolation, JS interop) belong to [1.8 Razor and Blazor](#18-razor-and-blazor), which is
where they are tracked.

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

- no parser or type model, so no `find_symbol`, references, callers, rename or refactoring *in* the
  JS/TS — the relative imports do form a module graph (`js_files`), which is what `analyze_js` checks
  for cycles;
- no JSX/TSX component analysis, props/state checks or bundler configuration understanding;
- no DOM for HTML/CSS: `markup_files`/`analyze_markup` scan tags and apply HTML0001–HTML0015 and
  CSS0001–CSS0009 (section 1's table) — the CSS rules also run on every `<style>` block — and resolve a
  selector against the page index, compound parts (tag, classes, id, attribute conditions, the
  structural pseudo-classes) joined by descendant, child and sibling combinators. A selector whose
  meaning only a browser can decide is indexed but not reported; specificity, the cascade and CSS built
  in JavaScript are not resolved;
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
schema comparison, and no language injection — SQL embedded in a C# string is reached by `trace_data_flow`
as a sink, but its text is not parsed or highlighted as SQL.

EF Core is visible only from the C# side: `DbContext`, `DbSet<>`, entity classes and configuration
classes are ordinary C# and get the full C# tool surface. **Implemented** (2026-10-30): `ef_migrations`
runs the project's own `dotnet ef` (list, add, script, remove, update, info, dbcontext-list) and
returns the command, exit code, stdout and stderr as structured data.

Still missing:

- a field-level comparison of the model itself against the snapshot — drift is detected by
  `ef_migrations action=pending-model-changes` (EF Core 8+) and the SQL between two migrations is
  `action=diff`, but the model's own fields are not compared against the snapshot's;
- inspection of the SQL a single query will generate (`action=script` returns the migration SQL);
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
- Kubernetes: `k8s_*` wraps kubectl (status, context, get as rows, describe, logs, kustomize render,
  exec, apply/delete/rollout with `confirm`) and `k8s_helm` wraps releases; still missing are
  manifest/chart schema validation and port-forward;
- YAML schema validation for compose/k8s manifests: `yaml_files`/`analyze_yaml` read the structure
  (YAML0001–YAML0012) and a manifest's `kind`/`apiVersion`/`name`, but no schema is applied.

### 1.8 Razor and Blazor

What exists is real and documented in [razor.md](razor.md): page inventory, 17 markup/accessibility rules
(RZ2001–RZ2017), 11 security rules with CWE ids (RZ1001–RZ1011), verified edits through `razor_edit`, and
a Razor→metacode frontend. The anti-forgery rule follows the partials a form renders — `<partial name=…>`,
`@Html.Partial("…")`, the `RenderPartial` family — so a token that lives in a shared partial counts.

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
| WPF / WinUI 3 / MAUI / Avalonia / UWP | ⚠️ C# analysed; XAML by the markup layer (`xaml_pages`, `analyze_xaml`, `xaml_bindings`, `xaml_tree`, `xaml_navigate`, the verified edits) — the project's dialect is detected, a binding path is checked against the type the markup names, and a type the markup does not name is "not checked" rather than guessed (section 1.4) |
| gRPC | ⚠️ generated C# analysed; `.proto` files are not parsed |
| SignalR | ⚠️ hubs are C#; no hub-contract analysis. Runtime client tools (`signal_r_*`) exist to exercise a hub |
| Unity / Godot / MAUI workloads | ⚠️ whatever MSBuild can restore is loaded; no specialist project support |
| Legacy non-SDK projects | ⚠️ loaded through MSBuild; `update_framework` deliberately leaves `net4x`/`netstandard` alone |
| Solution/project creation | ❌ no project templates: `templates` creates a file inside an existing project, `aspire` is the only project scaffolder, and `add_projects_to_solution` adds existing projects |

---

## 2. Debugger

**Implemented** (2026-11-01): a Debug Adapter Protocol client over
[netcoredbg](https://github.com/Samsung/netcoredbg), installed on demand (`debug_install`), with 20
`debug_*` tools — the 19 below plus `debug_test`, which drives a test host under the same debugger
([§4](#4-test-workflow)) — the whole surface the adapter exposes over DAP, plus one Roslyn capability on
top of it:

- **The session**: `debug_status`, `debug_install`, `debug_launch` (breakpoints in the same call, waits
  for the first stop), `debug_attach`, `debug_continue`, `debug_step` (over/into/out), `debug_pause`,
  `debug_output`, `debug_terminate`, `debug_sessions`, and `debug_restart` — the same program,
  arguments, environment and breakpoints again, with `rebuild=true` to run `dotnet build` first.
- **Breakpoints**: `debug_breakpoints` (line, C# condition and a hit count — the adapter has no hit
  counter, so the counting is here and `hitConditionSkips` reports what it absorbed),
  `debug_function_breakpoints` (a method by name, the breakpoint that survives an edited file), and
  `debug_data_breakpoints` (a field or property: **every write site the solution's source has**,
  resolved by Roslyn into line breakpoints, listed with their kind — the adapter has no
  `setDataBreakpoints`, so the sites are the data).
- **Exceptions**: `debug_exceptions` takes the adapter's filters (`all`, `user-unhandled`) **and the
  types that narrow them** (`filterOptions`, matched on the full type name, `!Type` to exclude), and
  with no setting to change it returns what the process is stopped on — id, stage, type, message and
  the nested inner exceptions. A stop on an exception carries the same block automatically.
- **Inspection**: `debug_threads`, `debug_stack` (file:line per frame), `debug_variables`
  (Locals/Arguments/Autos, one level of expansion) and `debug_evaluate`, which also **writes**:
  `newValue` is the debugger's Set Value, so a breakpoint plus an assignment changes state and
  `debug_continue` resumes with it.

Sessions survive across tool calls, so a stop that happened between two calls is still there.

Two things stay outside, and neither is a missing tool. **Edit-and-continue and hot reload** need the
metadata deltas the adapter applies only over GDB/MI; the substitute — `debug_restart rebuild=true`
plus value editing — and the exact reason are in
[reference/limits.md](reference/limits.md#debugger). The **debugger windows** (breakpoints list, call
stack, modules, parallel stacks) are UI: the data behind them is in `debug_sessions`, `debug_stack` and
`debug_threads`, and rendering it is the client's job ([section 10](#10-ui-bound-features--not-applicable-by-design)).

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
| ReSharper's inspection catalogue | ⚠️ `resharper_inspection_map` reads the real catalogue from the Rider installation (1,560 ids in Rider 2026.2.1, 910 of them applicable to C#, each with group, analyzer and description) and then the curated state: **19 ids are implemented natively under ReSharper's names** in the opt-in `rider` inspection pack, with a code fix for 16 of them (`ConvertToConstant_Local`, `LoopVariableIsNeverChangedInsideLoop` (no fix), `MemberCanBeMadeStatic_Global`, `RedundantArgumentDefaultValue`, `EmptyConstructor`, `RedundantIfElseBlock`, `ConditionIsAlwaysTrueOrFalse` (no fix), `RedundantBaseConstructorCall`, `ConvertToCompoundAssignment`, `RedundantLambdaSignatureParentheses`, `StringLiteralAsInterpolationArgument`, `RedundantCheckBeforeAssignment`, `UseNameofExpression`, `RedundantStringInterpolation`, `ReplaceSequenceEqualWithConstantPattern`, `RedundantPatternParentheses`, `MergeIntoPattern`, `ReplaceWithStringIsNullOrEmpty`; `MethodOverloadWithOptionalParameter` has no fix). Six were ported from the decompiled ReSharper analyzers of this installation (`RedundantInterpolatedStringExpressionAnalyzer`, `MethodOverloadWithOptionalParametersProblemAnalyzer`, `ReplaceSequenceEqualWithConstantPatternProblemAnalyzer`, `RedundantPatternParenthesesProblemAnalyzer`, `SequentialPatternCheck`, `ReplaceWithStringIsNullOrEmptyWarning`) — the rest of the catalogue is closed-source logic over JetBrains' PSI, so each port is a reimplementation against Roslyn, not a registration. 26 more map to an existing capability (`simplify_code` IDE id, a Roslyn pack, a dedicated tool, a context action); 8 well-known ids have none. The map also names the mapped ids this Rider version no longer declares. **The whole catalogue — all 1,560 ids, every language group, applicable to C# or not — is one row per id in [resharper_tocover.md](resharper_tocover.md)**, with what each rule reports and its coverage; `resharper_inspection_map outputPath=…` rewrites that page from the same read. |
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
CamelHumps → contains), `source`, `il` (the same symbol as IL: the C#↔IL toggle), `references`
(Roslyn's symbol finder inside the decompiled set), `outline`, `unload`. Go-to-definition and
find-usages on code without sources are no longer impossible.

Still missing:

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
- **41 context-action ids** through `context_action`: `import_type`, the argument guards
  (`check_param_null`, `check_all_params_null`, `check_string_not_null_or_empty`,
  `check_collection_not_empty`, `check_variable_null`, `check_dictionary_key`, `check_enum_defined`,
  `check_index_range`), the wrap actions (`wrap_in_try_catch`, `wrap_in_try_finally`, `wrap_in_if`,
  `wrap_in_using`, `wrap_in_region`), the string re-encodings (`string_to_raw`, `string_to_verbatim`,
  `string_to_regular`), the statement family (`add_braces`, `foreach_to_for`, `for_to_foreach`,
  `invert_loop`, `if_to_switch`, `concat_to_interpolated`, `interpolated_to_concat`),
  `digit_separators` / `remove_argument_name`, the second batch (`merge_sequential_checks`,
  `split_and_in_if`, `join_null_check_with_usage`, `use_var`, `use_explicit_type`, `split_declaration`,
  `remove_braces`, `migrate_to_nullable`, `remove_digit_separators`, `use_configure_await_false`) and
  the third (`merge_patterns`, `concat_to_stringbuilder`, `linq_query_to_method`,
  `linq_method_to_query`, `convert_to_extension_block`);
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
| Literal/string actions | raw/verbatim/regular re-encoding, both directions of interpolation↔concatenation, digit separators and the StringBuilder rewrite (`concat_to_stringbuilder`) are covered; still missing escape/unescape of individual characters and split/join |
| Statement actions | `foreach`↔`for`, add-braces, `if`-chain→`switch`, `invert_loop`, merge/split conditions, remove-braces, LINQ in both directions, pattern merging and the extension block are covered; still missing statement ↔ expression |
| Type actions | a DTO from JSON (`generate_dto_from_json`) and a builder (`generate_builder`) are covered; still missing a DTO from XML and a comparer |
| Test actions | `generate_tests` scaffolds a class; still missing "add a test for this member" and "run the covering test" |
| Editor policy | naming rules, and `use_var`/`use_explicit_type` as explicit actions (`IDE0007`/`IDE0008` are deliberately not style rewrites), are covered; still missing code-style enforcement on save and licence-header insertion (`IDE0073` needs the repository's template) |
| Regex and injections | regex completion/validation, and language injections (SQL/HTML/CSS/JSON embedded in a C# string is just text) |

The split matters when comparing numbers with ReSharper: **97 style ids ≠ 41 context actions**, and
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
[section 10](#10-ui-bound-features--not-applicable-by-design).

---

## 8. Architecture, diagrams and exploration

The numbers and the diagram data are both here; what remains is the rendering, which an MCP server
returns as text for the client (section 10):

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

## 9. Build, run and packaging

Build and run are covered (`build_project`, `run_project`, `run_tests`, `run_benchmarks`,
`quick_benchmark`, `run_plan`, `add_ci_workflow`). **Packaging is covered since 2026-10-30**
(`dotnet_publish` with RID/self-contained/single-file, `nuget_pack`, `nuget_push`) together with
**project references** (`add_project_reference`, `remove_project_reference`). **The build settings are
covered since 2026-10-31**: `analyze_binlog` reads (or produces and then reads) an MSBuild binary log —
errors and warnings with project and line, every project's and target's duration, the target that
dominates the wall clock and the project graph; `project_properties` reads the *evaluated* MSBuild view
(what `Directory.Build.props` and conditions contribute) and sets/unsets one property in the project or
the props file under a build gate; `run_config` stores launch shapes and `run_project configName=…`
applies one; `launch_settings` and `publish_profile` read and write `launchSettings.json` and `*.pubxml`
as data; `nuget_sources` manages the package sources and their credentials through `dotnet nuget`. What
remains:

- MSIX, ClickOnce, WiX/installer authoring;
- no `.http` request authoring: `run_http_file` sends the requests a `.http`/`.rest` file contains, but
  the file itself is edited as text and no schema validates it;
- no deployment targets (IIS, Azure, containers, Kubernetes);
- **no standalone command-line tool** for a pipeline (the equivalents of ReSharper's `inspectcode`,
  `dupfinder`, `cleanupcode`): the MCP server must be driven by an MCP client, so CI needs a harness;
- no feature-level migration: `update_framework` moves the TFM and `update_solution` moves packages, but
  there is no "enable nullable across the solution", "convert to file-scoped namespaces", "adopt primary
  constructors" campaign tool; `change_nullability` and `simplify_code` work per declaration/document.

---

## 10. UI-bound features — not applicable by design

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

## 11. Beyond ReSharper: SonarQube, CodeScene and the profiling IDEs

These families are covered, so what this section keeps is the **residual boundary** of each one — the
part of the SonarQube/CodeScene/IDE feature that is still absent. The
[capability map](../CAPABILITY_MAP.md#priority-gaps-vs-sonarqubecodescene) tracks them separately.

| Family | Covered by | What is still outside |
|---|---|---|
| Taint analysis (SQL injection/XSS across the data flow) | `trace_data_flow` (2026-10-31, reach widened 2026-11-01): request sources — query/form/header/cookie/body/route data, console and environment input, model-bound action parameters — followed through assignments, call arguments and a field written in one method and read in another, **on any object**, to the SQL, process, file, redirect, XSS and SSRF sinks, with the CWE and the path | A field is keyed by its declaration, not by the instance it hangs off, so the walk is instance-insensitive (writing one object's field taints every object's); a property, a database and a queue are not followed; the sanitizer list is explicit, so a sanitizer it does not name is a false positive to review; the answer is candidates with their path, not proofs |
| Mutation testing | `run_mutation_tests` (Stryker.NET): mutation score, killed/survived/timed-out/no-coverage counts and the surviving mutants with file and line | The runner is an external tool the user installs; the product drives it, it does not replace it |
| Database profiling, async/event-source viewers, instrumentation profiling | `profile_database` (EF Core/SqlClient/… commands, durations, N+1 shapes), `profile_async` (TplEventSource await durations, incomplete state machines), `profile_events` (any provider's inventory, the Events Viewer) and `profile_instrumentation` (IL rewrite with an injected probe: exact call counts and inclusive/exclusive times); `profile_root_cause` joins the measurements with the solution's metacode AST | Process-level only: no request tracing and no distributed trace across services; the root-cause join needs the PDB and the source on disk |
| Git-aware churn hotspots | `git_churn_hotspots` (2026-10-30): `git log --numstat` over a bounded window × the cyclomatic complexity of the file's worst method | Static (model + history), not a runtime measurement |

---

## 12. What to do today

This section used to be a workaround table. **Closed 2026-10-31**: every row is now either a tool in this
build or a boundary stated with its reason, and the rows whose feature already existed are deleted. The
table below is that boundary — the first column is the question, the second says which call answers it or
where the answer stops and why.

| You need | The tool — or where the boundary is |
|---|---|
| Read, search or edit a file no frontend reads (JSON, config, Markdown, C/C++, Python, an OData/XML manifest) | `read_file`/`read_files`, `search_text`, `text_index`. The text layer **is** the design for a file with no syntax model; it is not a missing parser. Python and C/C++ can also be lowered with `translate_python_file`/`translate_cpp_file` for comparison, not for analysis |
| HTML/CSS/SCSS diagnostics | `markup_files` and `analyze_markup` (HTML0001–HTML0015, CSS0001–CSS0009). Tags are scanned with a quote-aware reader and CSS is masked; a selector resolves against the page index — compound parts with tag, classes, id, attribute conditions and structural pseudo-classes, joined by descendant, child and sibling combinators — while a selector whose meaning only a browser can decide is indexed but never reported, and specificity and the cascade are not resolved (§1.5) |
| XAML diagnostics, bindings and edits | `xaml_pages`, `analyze_xaml` (XAML0001–XAML0015), `xaml_bindings`, `xaml_tree`, `xaml_navigate`, `xaml_rename`, `xaml_extract_style`, `xaml_promote_resource`, `format_xaml`. A binding path is checked against the type the markup names; a data type the markup does not name is `not-checked`, and the XAML itself is not compiled |
| SQL diagnostics | `sql_files` and `analyze_sql` (SQL0001–SQL0010). A pattern scanner, not a dialect parser: no schema, no execution, no query plan (§1.6) |
| JS/TS diagnostics and build | `node_info`/`node_install`/`node_script` for the toolchain; `node_typecheck` (tsc → `TSxxxx`) and `node_lint` (eslint → findings) for the diagnostics; `node_outdated`/`node_audit` for the dependency health; `js_files` and `analyze_js` (JS0001–JS0017) for the source itself, with a relative import resolved to a file and a module graph that reports its cycles. No type model or symbol table (§1.5) |
| EF Core migrations | `ef_migrations` (list/add/script/diff/remove/update/info/dbcontext-list/pending-model-changes): the migration SQL, the schema delta between two migrations, and whether the model still matches the snapshot. A field-level comparison of the model itself and per-query SQL inspection are not covered (§1.6) |
| Kubernetes and Helm | `k8s_status`/`k8s_context`/`k8s_get`/`k8s_describe`/`k8s_logs` read; `k8s_kustomize` renders an overlay offline; `k8s_exec` runs one confirmed command inside a pod; `k8s_apply`/`k8s_delete`/`k8s_rollout` write with `confirm=true` (apply defaults to a server-side dry run); `k8s_helm` covers releases. No manifest/chart schema validation, no port-forward (§1.7) |
| VB refactoring and editing | `apply_rename`, `encapsulate_field`, `add_params`/`remove_params`, `change_signature` (declaration and every plain call site, call parentheses optional), `change_type`, `use_base_type`, `type_to_partial`, `safe_delete`, `extract_interface` (an `Interface` plus the `Implements` clause on the type and on each member), `extract_method`, `move_type`, `move_to_folder` (the folder derived from the `Namespace` block), `property_to_auto`, `function_to_property` (both call spellings) and `simplify_vb_code` (41 ids) apply; `context_action` on a `.vb` file implements the 33 ids that have a VB spelling (guards, Try/If/Using wrapping, Imports, interpolation, digit separators, named arguments, use var/explicit type, split declaration, StringBuilder, merge/split checks, conditional access, `Select Case`, LINQ both ways, For Each↔For, invert loop); `apply_code_fix` runs the .NET analyzers' VB fixers. What is still C#-only is the declaration-shape tail of the catalogue (§1.2) |
| F# analysis | `fsharp_files` lists the declarations (modules, types, functions, opens) and `analyze_fsharp` applies FS0001–FS0010; `build_project` returns the compiler's FS#### diagnostics, `run_tests` runs F# tests, `search_text`/`text_index` read the files. No symbols, references or refactorings: F# is not a Roslyn language (§1.3) |
| Debug a process | `debug_launch`/`debug_attach` with line, function and data breakpoints, per-exception-type filters, stepping, `debug_stack`, `debug_variables`, `debug_evaluate` (read or write), `debug_restart` (20 `debug_*` tools); `profile_stacks`/`profile_dump`/`profile_slowdown` remain the post-mortem path (§2) |
| Single test | `run_test` (name, class or raw filter, TRX result) and `rerun_failed`; `debug_test` for the failing one (§4) |
| Inspection suppression and severity | `suppress_finding` (a `#pragma` around the member or a `SuppressMessage` attribute, compiler-gated) and `configure_inspection` (severity per scope); `inspection_baseline` gates CI (§3) |
| Decompiled symbol navigation | `assembly_workspace` (load, search, source, il, references, outline over a Roslyn workspace) and `inspect_assembly`/`find_assembly_api`. Rename and debugging inside the decompiled code are not possible: there is no source to write (§5) |
| Taint analysis across a data flow | `trace_data_flow` (sources, sinks, sanitizers and the path between them) and `find_throw_sites` for "which methods throw this exception"; the reach — locals, parameters, call arguments to a caller-set depth, plus a field written by one method and read by another on any object — is stated in the answer, together with the instance-insensitivity that makes it an over-approximation (§11) |
| Real-time collaborative editing | A shared transport and an editor surface, not a local stdio tool (§10) |

---

## 13. How this inventory is maintained

- The ✅ side lives in [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md). This page is the
  ❌/⚠️ side and the reasons behind them.
- When a gap closes, delete its row here and add the tool to the coverage table; do not leave a "was
  missing" note.
- When a new tool ships, check whether it removes a row here — the two pages are meant to sum to the same
  product, not to be two independent claims.
- "Not applicable" rows stay: they are the boundary of the product, not a backlog.

---

## 14. Revision log

The batches that closed rows on this page, newest first. A count is the live inventory at the end of the
batch (`capability_report` prints the current one); the per-feature record of each batch is in
[CAPABILITY_MAP.md](../CAPABILITY_MAP.md).

| # | Date | What closed | Tools |
|---:|---|---|---|
| 32 | 2026-11-01 | **The Visual Basic refactoring tail**: six refactorings that used to stop at a C# syntax tree now run on a `.vb` document under the same compiler gate — `extract_interface` (an `Interface` in VB's vocabulary, plus the `Implements` clause VB requires on the class *and* on each member), `extract_method` (a contiguous block into a private `Sub`/`Function`, the locals and parameters it reads becoming its parameters, refused on a jump or a write), `move_type` (the declaration into a file of its own, carrying the file's `Imports` and its `Namespace`), `property_to_auto` (a trivial `Get`/`Set` pair into an auto-`Property`, dropped backing field; refused when the field is read elsewhere), `function_to_property` (a parameterless `Function` into a `ReadOnly Property`, call sites in both VB spellings, `GetCount()` and `GetCount`) and `move_to_folder`, whose folder derivation now reads a VB `Namespace` block. Seven tests, one per path and one refusal | 416 (unchanged) |
| 31 | 2026-11-01 | **§2 Debugger, closed against the adapter's real surface**: the DAP requests netcoredbg does implement are now all exposed — **per-exception-type filters** (`filterOptions`: full type name, `!Type` to exclude) and the stopped exception's own block (`exceptionInfo`, returned by `debug_exceptions` and by every stop on an exception), **function breakpoints** (`setFunctionBreakpoints`), **Set Value** (`setExpression` through `debug_evaluate newValue`), and **hit conditions counted here** because the adapter ignores them. Two features the adapter has no DAP request for are delivered anyway: **data breakpoints** as the solution's Roslyn-resolved write sites (`debug_data_breakpoints`) and **restart with a rebuild** (`debug_restart`) as the edit → build → run loop. Only edit-and-continue/hot reload stays out — the adapter applies metadata deltas over GDB/MI only — and it is recorded in [reference/limits.md](reference/limits.md#debugger), not here | 413 → 416 |
| 30 | 2026-11-01 | **Four §12 boundaries narrowed**: `assembly_workspace action=il` returns the same decompiled symbol as IL, so the C# and IL views are one call apart; `k8s_kustomize` renders an overlay offline and `k8s_exec` runs one confirmed command inside a pod (port-forward stays out); `ef_migrations action=diff` scripts the SQL between two migrations; `analyze_markup`/CSS0009 resolve compound selectors — descendant/child/sibling combinators, attribute conditions and the structural pseudo-classes — against a per-page element index, and a selector with a dynamic pseudo-class is indexed but never reported. Specificity, the cascade, a model-vs-snapshot field diff and port-forward remain outside | 411 → 413 |
| 29 | 2026-11-01 | **Three limits closed by extending the tool that named them**: `trace_data_flow` follows a field written in one method and read in another **on any object** (before, only a field of the reading type), and the answer states the instance-insensitivity that makes it an over-approximation; `analyze_razor_pages`/`find_razor_vulnerabilities` resolve the partials a form renders (`<partial name=…>`, `@Html.Partial("…")`, the `RenderPartial` family, nested with a cycle guard), so RZ1005 no longer fires on a form whose token lives in a shared partial; `change_signature` gains a **VB path** — the declaration and every plain call site, call parentheses optional, `Optional` emitted for an added default, refused on an `AddressOf` use | 411 (unchanged) |
| 28 | 2026-10-31 | **The Visual Basic tail**: `simplify_vb_code` rewrites 24 more VB ids (the qualifier, the `If`/`Else` folds, a completed `Select Case`, object initializers, tuple element names, `NameOf`, interpolation, both LINQ chains, `HashCode.Combine`, modifier order, accessibility, `ReadOnly`, an unused member, an unread field, unreachable code, a dead store, an unused parameter, an auto-property and the three layout rules) — 41 of the 49, with 8 refused by id and the reason; `change_type`, `use_base_type`, `type_to_partial` and `safe_delete` gain a VB path, so seven refactorings now run on a `.vb` document. The C#-syntax members of the refactoring catalogue keep needing their own path | 404 (unchanged) |
| 27 | 2026-10-31 | **The HTML/CSS limit narrows**: `markup_files` indexes every page's tag/class/id set and every stylesheet's simple selectors; `analyze_markup` gains **CSS0009** (a `tag`/`.class`/`#id`/`tag.class` selector that matches no element in the scanned pages) and **HTML0015** (a class used in markup that no scanned stylesheet defines), and the CSS rules and selectors now run on every `<style>` block with the finding reported on the page's line. The match is lexical and set-against-set: combinators, attribute selectors, pseudo-classes, specificity and the cascade stay out, a CDN stylesheet or CSS-in-JS is outside the scan | 404 (unchanged) |
| 26 | 2026-10-31 | **Three boundaries narrowed**: `list_generated_sources includeSource=true` returns the text of the trees a source generator emits (before, only their metadata); `js_files` builds the module graph from the relative specifiers and reports its import cycles (before, no graph); `ef_migrations action=pending-model-changes` answers whether the model still matches the migration snapshot | 404 (unchanged) |
| 25 | 2026-10-31 | **The depth of the scanned layers and the data-flow reach**: `analyze_yaml` gains YAML0009–YAML0012 (kind without apiVersion or the reverse, metadata with no name, a container with no image, two containers with the same name); `analyze_js` resolves a relative import against the file system and reports JS0017; `trace_data_flow` follows a value stored in a field of the same type, across the method that writes it and the method that reads it | 401 (unchanged) |
| 24 | 2026-10-31 | **Visual Basic, the last two C#-shaped surfaces.** `context_action` routes a `.vb` document to a VB engine of 33 ids (guards, wrapping, Imports, interpolation, digit separators, named arguments, var/explicit type, split declaration, StringBuilder, merge/split checks, conditional access, `Select Case`, LINQ both ways, For Each↔For, invert loop) and refuses the C#-only ones with the reason; `apply_code_fix` selects analyzers **and fixers** by language, so the .NET analyzers' 100+ VB `CodeFixProvider`s run on a `.vb` document (CA1822 → `Shared`) | 401 (unchanged) |
| 23 | 2026-10-31 | **Section 9 leaves this page.** Version control and collaboration is now a feature section of [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md): the 28 Git tools (27 `git_*` and `commit_lint`), the 8 GitHub tools and `code_review`, 37 in all. The only remaining boundary, real-time co-editing, is the UI-bound table (§10) | 401 (unchanged) |
| 22 | 2026-10-31 | **The board becomes the source of truth**: `board_brief` (counts, the card to pick up, staleness) and `board_audit` (each open card cross-checked against the live tool and context-action catalogue plus the paths it names, with `close=true`), `board_move note=` as the recorded definition of done, a `board` block on every non-empty `read_project_docs`, and the "what is missing → the board" routing in the server instructions and the installed `AGENTS.md` block | 399 → 401 |
| 21 | 2026-10-31 | **Disk hygiene**: `disk_usage` — the report of every folder a .NET session grows, safe cleanup per target, and a redirect guard persisted in `.visualMCP/disk-policy.json` | 398 → 399 |
| 20 | 2026-10-31 | **Version control: the last points**: `commit_lint` (Conventional Commits as data) and `git_commit conventional=true`; `git_host` gains `review-pr`, `close-pr`, `reopen-pr` and `update-pr` on GitLab, Azure DevOps and Bitbucket | 397 → 398 |
| 19 | 2026-10-31 | **The last four open points of the coverage page**: the scanned layers for JS/TS, HTML/CSS, YAML and F# (`js_files`/`analyze_js`, `markup_files`/`analyze_markup`, `yaml_files`/`analyze_yaml`, `fsharp_files`/`analyze_fsharp`), `trace_data_flow`, `find_throw_sites`, five context actions and CS1729 | 387 → 397 |
| 18 | 2026-10-31 | **Build settings: the last rows** — `analyze_binlog`, `project_properties`, `run_config`, `launch_settings`, `publish_profile`, `nuget_sources` | 381 → 387 |
| 17 | 2026-10-31 | **Profiling**: `profile_root_cause` joined to the metacode AST, `profile_async`, `profile_database`, `profile_events`, `profile_instrumentation` | 376 → 381 |
| 16 | 2026-10-31 | `get_quality_trend`; the DTO half of `find_missing_validation` | 375 → 376 |
| 15 | 2026-10-31 | `templates` and `layout_code`, `code_cleanup profile=`, the fuzzy `find_symbol` fallback, ten context actions, CS0117/CS1501/CS7036 in `create_from_usage` | 373 → 375 |
| 14 | 2026-10-31 | **What to do today**: the `k8s_*` family (9), the `node_*` family (7), `sql_files`/`analyze_sql` | 355 → 373 |
| 13 | 2026-10-31 | **Visual Basic as a language surface**, **refactoring gaps** (`make_non_static`, `transform_parameters`, `move_type_to_nested_scope`, `generify`) and most of **version control** (`git_conflicts`, `git_rebase action=plan`, `git_patch`, `git_host`, `gh_pr` review/checks, `gh_branch_protection`, `code_review`) | 346 → 355 |
