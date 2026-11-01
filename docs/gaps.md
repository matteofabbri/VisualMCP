# What is missing — the gap inventory

This is the complete list of what VisualMCP does **not** do. It complements
[ReSharper & CodeMaid — Feature Coverage](resharper-codemaid-coverage.md), which is the ✅ side: every row
here is a feature a user could reasonably expect from an IDE and will not find in this product.

A different axis — how many calls an agent needs to answer one question, measured on the session
store — is [Agent ergonomics](agent-ergonomics.md). That page is a backlog of tool-surface work;
this one is the feature inventory.

Two kinds of absence are kept apart, because they need different decisions:

| Mark | Meaning | What to do about it |
|---|---|---|
| ❌ | A real gap. The feature exists in ReSharper/Rider and could be built here. | Implement it; the [cross-cutting table](resharper-codemaid-coverage.md#cross-cutting-boundaries) states the boundaries that are properties of the analysis rather than missing features |
| ⚠️ | Partial. Something exists, and the row says exactly what is missing. | Extend the existing tool |

Last updated: 2026-11-01 — **429 tools** in the running build (`capability_report` prints the live
count). Version control and collaboration is not on this page: it is a feature section of
[resharper-codemaid-coverage.md](resharper-codemaid-coverage.md), because none of it is a gap. The
batches that closed rows here, each with the count it produced, are in
[§10 Revision log](#10-revision-log); anything older is in `git log`.

---

## At a glance

| Area | State | Priority | Why this priority |
|---|---|:---:|---|
| [Language and platform scope](#1-language-and-platform-scope) | ⚠️ | Low if your solution uses them | C#, VB, XAML, Razor, EF Core, Kubernetes and the Node toolchain are covered semantically or by a wrapper; SQL, JS/TS, HTML/CSS, YAML and F# each have a **scanned** layer with an inventory and a rule set, and JS/TS and F# also have a **lexical symbol table** (`js_symbols`, `fsharp_symbols`: declarations, the declaration a name resolves to, its references). What none of the scanners does is type-check an expression |
| [Debugger](#2-debugger) | ✅ | — | Implemented: 20 `debug_*` tools over netcoredbg (DAP) — line, function and data breakpoints, per-exception-type filters, value editing and restart. Edit-and-continue is the one thing the adapter exposes only over GDB/MI, so it is an engine limit ([§2](#2-debugger)) rather than a missing tool |
| [Inspection administration](#3-inspection-administration-and-background-analysis) | ✅ | — | Severity, suppression, custom rules, `.DotSettings` reading, naming rules, spelling and a warm analysis loop exist; only the proprietary ReSharper id set is absent |
| [Decompiled code as a workspace](#4-decompiled-code-as-a-workspace) | ✅ | — | `assembly_workspace` decompiles into a Roslyn workspace: search, source, references, outline |
| [Context actions and templates](#5-context-actions-templates-and-generation) | ✅ | — | 54 action ids and the template family (18 live, 10 postfix, 8 file) exist: the import family, the wraps, both directions of every string and statement shape, the guards, the extension block, `""`↔`string.Empty`, an auto-property made explicit and the `.editorconfig` file header. Contract annotations and language injections are boundaries (§5) |
| [Refactoring gaps](#6-refactoring-gaps) | ✅ | — | All ~51 of Rider's main set are implemented, including `transform_parameters`, `make_non_static`, `move_type_to_nested_scope` and `generify` (2026-10-31) |
| [Diagrams and exploration](#7-architecture-diagrams-and-exploration) | ✅ | — | Graph artifacts (project/namespace/type, call, sequence, control flow), value tracking, localization and the inventories exist as tools and artifacts |
| [Build, run, packaging](#8-build-run-and-packaging) | ✅ | — | Build/test/run/publish, the six build-settings tools, `project_template` (`dotnet new`), `installer_project` (WiX v4/v5 and ClickOnce), `deploy_target` (IIS through a Web Deploy profile, Azure Web App and Static Web App), `migration_campaign` (four solution-wide rewrites) and the standalone `--cli` (inspect/duplicates/cleanup) |

---

## 1. Language and platform scope

The semantic engine is **Roslyn**, and it reads the two languages a .NET solution can be written in:
**C#** and **Visual Basic**. Everything else is one of six things:

- a dedicated **Razor/Blazor markup scanner** (heuristics over `.cshtml`/`.razor`),
- a **markup layer** for XAML, with the C# code-behind analysed through Roslyn,
- a **pattern scanner** per file kind — SQL, JavaScript/TypeScript, HTML/CSS, YAML and F# — where
  comments and literals are masked first and the rules that need no type information are applied
  (`analyze_sql`, `analyze_js`, `analyze_markup`, `analyze_yaml`, `analyze_fsharp`),
- an **inventory** per file kind (`sql_files`, `js_files`, `markup_files`, `yaml_files`,
  `fsharp_files`): what the file declares, which modules it imports, the ids of a page, the
  `kind`/`apiVersion` of a manifest,
- a **lexical symbol table** for the two scanned languages a token can follow — `js_symbols` and
  `fsharp_symbols`: the declarations, the declaration a name resolves to, and its references, with no
  type model behind them,
- **text** — `search_text`, `text_index` (FTS5), `read_file`, and any tool that does not need a syntax
  tree.

Around those, the non-.NET toolchains are wrapped rather than reimplemented: `node_*` runs the project's
own npm/pnpm/yarn, `tsc` and `eslint`; `k8s_*` runs `kubectl` and `helm`; `docker_*` runs the engine. The
wrapper reports a missing client instead of installing one.

The full-text index deliberately accepts many extensions, so every file a repository carries is
*findable*. A scanned language is the next step: its rules are read from masked source and stated one
by one, and where a token can prove a binding, a **lexical** symbol table resolves it — which is a name
resolution over masked source, not a type model. What no scanner here does is type-check an expression;
that is the distinction this section keeps.

| Language / platform | What works today | What is missing |
|---|---|---|
| **C#** | The full surface: semantic navigation, references, callers, refactorings, style, inspections, complexity, profiling, decompilation | The general boundaries in the [cross-cutting table](resharper-codemaid-coverage.md#cross-cutting-boundaries): dynamic dispatch is not modelled, and a static call graph needs `find_reflection_usage` / `framework_entry_points` to cover reflection and framework conventions (both shipped); behaviour is measured by `verify_change` when the suite covers the change |
| **Visual Basic** | The **symbol index** (declarations, uses by kind, hierarchy, outline, metacode, unused); **metacode** (translate, compare, index trees and complexity, generate); the scoped tools over both languages (`structural_search` with VB patterns, `format_code`, `optimize_usings` for `Imports`, `code_cleanup` including member reordering); **inspections** through the packs that ship VB analyzers (.NET analyzers, the VMC0001 house rule) and `inspection_baseline`; **analysis** (`find_dead_code`, `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns`, `complexity_report`, `get_complexity_hotspots`, `get_metrics`, `analyze_complexity`, `get_cohesion_metrics`, `find_duplicate_code`); **refactorings and generation** (`apply_rename`, `encapsulate_field`, `add_params`/`remove_params`, `generate_docs`, `implement_members`, `reduce_complexity`/`fix_complexity`); `simplify_vb_code` (**41 of the 49 VB ids**, the other 8 refused by id with a reason); `context_action` on a `.vb` document (**33 ids** with a VB spelling) and `apply_code_fix` with the analyzers' VB fixers; `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `run_code_analysis`; text search/index; Git and file tools | The refactorings that are C# syntax or C# idiom (primary constructors, records, file-scoped namespaces, expression bodies, `using` static, the pattern combinators) — they refuse with that language reason; the C#-only inspection packs report that they do not cover a VB project; the declaration-shape refactoring tail listed in §1.2 (`extract_class`/`extract_superclass`, `inline_class`/`inline_parameter`/`inline_symbol`, `move_instance_method`/`move_static_members`, `move_type_to_nested_scope`/`move_type_to_outer_scope`, `function_to_indexer`/`indexer_to_function`); `reduce_complexity` reports extract-method as a hand edit |
| **F#** | `fsharp_files` — the declaration inventory (modules, types, top-level functions, opens); **`fsharp_symbols`** — the lexical symbol table: the declarations a file makes (modules, namespaces, types, union cases, record fields, `let` bindings and members, with their positions), the declaration a name resolves to (before it in the same file, or in a file that `open`s its module), every reference, and the declarations nothing reads; `analyze_fsharp` — FS0001–FS0010 over masked source (tabs, `Option.get`, partial list access, `Unchecked.defaultof`/`unbox`, null comparison, `with _ ->`, `let mutable`, `printf`, `failwith`, a duplicated `open`); `build_project` returns the F# compiler's own diagnostics (FS####); `run_tests` runs F# tests | No semantic model: F# is not a Roslyn language, so there is no type checking, no refactoring, no inspection and no complexity; a lexical `rename` exists in `fsharp_symbols`. The symbol table is lexical — shadowing inside a match arm, a module abbreviation and a name the compiled assembly exports are outside it — and the compiler's FS#### diagnostics remain the authority |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | `xaml_pages` (inventory + the project's dialect and target frameworks), `analyze_xaml` (XAML0001–XAML0015, binding paths validated against the data type), `xaml_bindings` (the binding inspector), `xaml_tree` (the element tree), `xaml_navigate`; verified edits `xaml_rename`, `xaml_extract_style`, `xaml_promote_resource`, `format_xaml`; the C# code-behind is fully analysed. The data type comes from `x:DataType`/`DataType` in scope, a code-behind `DataContext = …`/`BindingContext = …`, a design-time `d:DataContext`, or a root `<Root.DataContext><local:ViewModel/></Root.DataContext>` property element | No compiled XAML: a binding whose data type none of those names is `not-checked` rather than guessed, and a key from a referenced assembly's theme dictionary is invisible |
| **JavaScript / TypeScript / JSX / TSX** | `node_info`, `node_install`, `node_script` (the project's npm/pnpm/yarn); `node_typecheck` (tsc → `TSxxxx` diagnostics with file:line), `node_lint` (eslint → findings with rule and position), `node_outdated`, `node_audit`; `js_files` (imports, exports, functions, classes, JSX, a module graph whose specifiers are resolved through relative paths, the nearest `tsconfig.json` `baseUrl`/`paths` and `node_modules`, with its import cycles) and `analyze_js` (JS0001–JS0017 over masked source; JS0017 is a relative import that names no file); **`js_symbols`** — the lexical symbol table: the declarations a file makes (functions, classes and their members, variables and destructured bindings, parameters, TypeScript interfaces/types/enums, import bindings), the declaration a name resolves to in the innermost enclosing scope, every reference inside the module and through the imports other modules take from it (the specifier resolved the same way), JSX component tags as references, and the declarations nothing reads | No type model: an imported name resolves through its binding when the specifier resolves to a module in the scan (a relative path, a tsconfig alias or a package in `node_modules`), a runtime alias neither `tsconfig.json` nor `node_modules` answers is not guessed, a member reached through an untyped object, a computed member name and `eval` are not resolved, and there is no refactoring and the lexical `rename` is not type-aware; no bundler or component analysis |
| **HTML / CSS / SCSS** | `markup_files` (elements, distinct classes, selectors, ids, duplicates) and `analyze_markup`: HTML0001–HTML0015 (alt, lang, viewport, duplicate id, label binding, nested form, deprecated element, inline handler, `target="_blank"`, empty href, `button` type, control name, `http://` subresource, tag balance, a class no scanned stylesheet defines) and CSS0001–CSS0010 (`!important`, ID selector, empty rule, `@import`, duplicate selector, unbalanced braces, `javascript:` URL, `expression()`, a selector that matches no element in the scanned pages, a declaration overridden on every element it matches), with the CSS rules and selectors also read from every `<style>` block and the safety rules checked on an inline `style` attribute | A structural scanner, not a renderer. A selector resolves against the page index — compound parts with tag, classes, id, attribute conditions and the structural pseudo-classes `:root`/`:first-child`/`:last-child`/`:only-child`/`:empty`, joined by descendant, child, adjacent-sibling and general-sibling combinators — on the nesting the tags write. A selector whose meaning only a browser can decide (`:hover`, `:checked`, `:nth-child()`, `:has()`) is indexed but never reported; CSS0010 resolves specificity and source order for a declaration that is overridden on every element it matches, while equal specificity across two different files is treated as unknown; a CDN stylesheet or a style built in JavaScript (CSS-in-JS) is outside the scan; no CSS-isolation analysis, no formatting |
| **YAML** | `yaml_files` (documents, top-level keys, and `kind`/`apiVersion`/`metadata.name` for a manifest) and `analyze_yaml`: YAML0001–YAML0008 (tab indentation, duplicate key, trailing whitespace, YAML 1.1 booleans, a colon glued to its key, a value that parses as a nested mapping, unbalanced flow collection, an alias with no anchor) and YAML0009–YAML0012 (kind without apiVersion or the reverse, a metadata block with no name, a container with no image, two containers with the same name); a Kubernetes manifest is also validated field by field offline by `k8s_validate` (K8S001–K8S023) | No schema for a CI pipeline file or a compose file, and no schema at all for YAML that is not a Kubernetes manifest: the structural rules are a line scan, `k8s_validate` is a table of the built-in kinds rather than the OpenAPI schema |
| **SQL** | `sql_files` (per-file statements, verbs and tables) and `analyze_sql` (SQL0001–SQL0012: lexical, correctness, safety, security and performance rules); the scan reads the DDL the files carry (`CREATE TABLE`, `ALTER TABLE … ADD`) into a schema model and checks a statement's table and column names against it (SQL0011 a table the scan does not declare, SQL0012 a column its declared table does not have), opt-out with `schema=off`; SQL built in C# is followed by `trace_data_flow` (a request value reaching `FromSqlRaw`/`ExecuteSqlRaw`/Dapper/`CommandText`) | No dialect parser and no live database: the schema is the one the scanned files declare, so a table created elsewhere is reported as undeclared, not as invalid. No execution, no query plan, no formatting |
| **EF Core** | The C# model — `DbContext`, entities, configurations — is analysed like any C#; `ef_migrations` runs the project's own `dotnet ef` (list, add, script, diff, remove, update, info, dbcontext-list, pending-model-changes) and compares the model to the migration snapshot field by field (`model-diff`: the properties added in the model, the snapshot members with no CLR member — usually shadow properties — and the type changes) | Model drift is detected by `pending-model-changes` and explained field by field by `model-diff`; `script` returns the migration SQL and `diff` the SQL between two migrations. No inspection of the SQL a single query will generate (use `profile_database` on a running process), no seeding help |
| **Kubernetes / Helm** | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs` read the cluster; `k8s_kustomize` renders an overlay offline; `k8s_validate` validates a manifest offline against a table of the built-in kinds (K8S001–K8S023); `k8s_port_forward` starts, lists, inspects and stops a port-forward session; `k8s_exec` runs one confirmed command inside a pod; `k8s_apply`, `k8s_delete`, `k8s_rollout` write with `confirm=true`; `k8s_helm` covers releases and lints a chart offline (`lint`); `yaml_files`/`analyze_yaml` read a manifest's identity and its structural mistakes | No OpenAPI/admission-schema validation offline: `k8s_validate` checks the required fields of the built-in kinds and the selector/template agreement, and `k8s_validate server=true` asks the cluster with `kubectl apply --dry-run=server`, which is the authority for a CRD's schema and a webhook's rules when a cluster answers; a port-forward is bound to the server process's lifetime |
| **Razor / Blazor** | Page inventory, 17 markup/accessibility rules (RZ2001–RZ2017), 11 security rules (RZ1001–RZ1011), verified edits, Razor→metacode | No Razor compilation: `@model` members, tag helpers, component parameters, cascading values, `@bind` types, CSS isolation and JS interop are unchecked |
| **Python / C++** | `metacode_translate` / `metacode_compare` take a `.py`/`.cpp` path (or `language=python|cpp` with source text) and run the dumper for it, and `translate_python_file` (CPython's `ast`) / `translate_cpp_file` (`clang -ast-dump=json`) return the dump itself; a saved dump is lowered too, with origin lines and a diagnostic per unmodelled construct | No type checker, no symbol navigation, no editing — the readers model syntax for comparison, not a language service |
| **Java, Rust** | `metacode_translate` / `metacode_compare` read `.java` and `.rs` (and `language=java|rust`): Java through `JavaFrontend`, Rust through `RustFrontend`, both recursive-descent readers over the source | No symbol navigation, no analysis, no editing |
| **Go** | `MetacodeLanguage` has a label | No frontend, no analysis, no navigation, no editing |

### 1.1 C#

C# is the only language with a complete tool surface, and it is where every other documentation page
applies. Its own boundaries are not language gaps and are recorded in
[the cross-cutting table](resharper-codemaid-coverage.md#cross-cutting-boundaries): a reflective name built at run time is
not guessed (so what that lookup reaches can still look dead), the trees a source generator emits are
reachable by `find_symbol`, `read_symbol` and `list_generated_sources` but are not documents (the editors,
the symbol index, `inspect_code` and `text_index` still do not see them; a text scan of the folder reaches
the emitted file), and anything that crosses the solution boundary is metadata rather than analysed source.

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
| Refactorings | ✅ the whole declaration-shape family: rename, `encapsulate_field`, the `ParamArray` pair, `change_signature` (the declaration and every plain call site, parentheses optional, `Optional` emitted for an added default), `change_type`, `use_base_type`, `type_to_partial`, `safe_delete`, `extract_interface` (an `Interface` plus the `Implements` clause VB requires on the type *and* on each member), `extract_method`, `extract_class` (a `Friend NotInheritable Class` beside the owner, a `Private ReadOnly` holder and a forwarding `Sub`/`Function`/`Property` per selected member), `extract_superclass` (the base in a file of its own plus the `Inherits` clause), `move_type`, `move_to_folder` (the folder derived from the `Namespace` block), `property_to_auto`, `function_to_property`, `inline_field` (an initializer substituted at every read, refused on a write or a cross-file use), `inline_symbol` (a local or a single-`Return` member), `inline_parameter` (the argument removed at every call site; refused when the sites pass different values or the value reads state), `inline_class`, `move_to_namespace` (the `Namespace` block renamed, the `Imports` added and the stale one removed), `property_to_function` (an auto-`Property` into `Get…`/`Set…`, reads rewritten, a write refused), `move_static_members` (a `Shared` member, references re-pointed), `move_instance_method` (a `Shared` method taking its receiver as `source As SourceType`), `move_type_to_nested_scope`/`move_type_to_outer_scope`, and `function_to_indexer`/`indexer_to_function` in VB's `Default Property`/`Get`/`Set` vocabulary. Closed again on 2026-11-01: `introduce_variable` (a `Dim` with the inferred type, every occurrence in the member rewritten), `introduce_field` (a `Private ReadOnly` field, refused when the expression reads a local, a parameter or `Me`), `introduce_parameter` (a constant, `Nothing`, `GetType(...)` or `NameOf(...)`, passed at every parenthesised call), `introduce_part_of_string` (a substring into `Private Const`, the literal rewritten with `&`), `invert_boolean` (every `Return` negated, optional rename), `copy_type` (`Sub New` needs no rename), `static_to_extension`/`extension_to_static` (the `<Extension>` attribute and the call-site receiver), `convert_anonymous_to_type` (`New With {.A = 1}` into a `Friend NotInheritable Class`), `implement_inpc` (`Implements INotifyPropertyChanged`, the event, the caller-name helper and one backing-field property per auto-property), `pull_members_up` (into an interface with `Implements I.Member` on every member, or into a base class) and `push_members_down` (into the single derived type that uses it), and `transform_parameters` for `make-optional`, `make-required` and `change-type`. Closed once more on 2026-11-01: `generify` (a method or a type, with the explicit type argument at every call site or reference and VB's `As { … }` constraints), `global_usings` (each file's plain `Imports` becomes a project-level `<Import Include="…" />` and the statement goes; the gate compiles the edited project with the new global imports applied to its options) and the `wrap`/`replace-with-property` operations of `transform_parameters` (a parameter object with a constructor, or a property on the receiver assigned before the call). What stays C#-only is only what VB cannot express: `convert_to_record`, `convert_to_primary_constructor` and `change_nullability` — VB has no records, primary constructors or nullable reference types |
| `simplify_code` | ✅ `simplify_vb_code` rewrites 41 of the 49 VB ids; the other 8 are returned with a reason, one per id, and five of them are constructs VB does not have |
| `structural_search`, `format_code`, `optimize_usings` | ✅ both languages; `code_cleanup` covers VB for style/usings/regions/format/reorder |
| `inspect_code` packs, `inspection_baseline` | ✅ the packs that ship VB analyzers run (netanalyzers, house) and `apply_code_fix` runs their VB `CodeFixProvider`s; the C#-only packs report that they do not cover the project |
| Context actions (`context_action`) | ✅ a `.vb` document routes to the VB engine: 33 ids with a VB spelling are implemented, the C#-only ones are refused with the reason |
| Symbol index (`reference_index`) | ✅ |
| Dead code, code smells, async/performance patterns, complexity report | ✅ |
| Metrics, cohesion, clones, Big-O (`get_metrics`, `get_cohesion_metrics`, `find_duplicate_code`, `analyze_complexity`) | ✅ |
| Generation and reduction (`generate_docs`, `implement_members`, `reduce_complexity`, `fix_complexity`) | ✅ except the extract-method move, which is reported as a hand edit |
| Metacode translate/generate | ✅ `VisualBasicFrontend` lowers modules, classes, structures, interfaces, enums, properties, methods, events and the statement/expression vocabulary; `With … End With`, `ReDim`, `On Error` and `RaiseEvent` stay `RAW[vb]` with a diagnostic, and `metacode_generate` emits C# from a `.vb` file |

A VB project keeps the symbol index, the inspections and their fixes, the analysis, the metrics, the
generation tools, the context actions, the style ids and **every refactoring in the catalogue except the
three VB cannot express** — a record, a primary constructor, a nullable reference type. Those three are
named in the [Visual Basic row of §1](#12-visual-basic).


### 1.3 F#

F# is a first-class .NET language but not a Roslyn language: `MSBuildWorkspace` loads C# and VB projects,
so the semantic engine has no F# model to give. What it does have is the compiler: `build_project` runs
`dotnet build` and parses the MSBuild log, which is where the F# compiler's own `FS####` diagnostics
appear; `run_tests` runs an F# test project, and `search_text`/`text_index`/`read_file` read the source.

What is absent, and stays absent as a product boundary rather than as a pending item: type checking,
refactorings, inspections and complexity. Those need FSharp.Compiler.Service — a second compiler platform
embedded next to Roslyn — and this product is a Roslyn server. **What it does have is `fsharp_symbols`
(2026-11-01)**: a lexical name table over the masked source — modules, namespaces, types, union cases, record
fields, `let` bindings and members with their positions; a name resolves to the declaration of the same name
before it in the file, and across files only through an `open` the other file declares. It answers "where is
this declared, what does this name refer to, who reads it, what is dead" without a compiler, and it says so
in every answer. `action=rename` renames one declaration and every reference the table resolved to it
(before it in its file, or in a file that opens the declaring module) — lexical, with no compiler gate. The rest of the boundary is in section 1's table and repeated in section 11 so a comparison
with an IDE stays honest.

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

**Implemented** (2026-11-01): a **lexical symbol table** over the same files. `js_symbols` tokenises the
masked source, opens a scope for every brace block and records a declaration for every name a declaration
position introduces — `function`, `class` (and its members, private ones included), a `const`/`let`/`var`
declarator with destructuring, a parameter, a TypeScript interface/type/enum, an import binding — with its
line, column, container and the names it is exported under. Every identifier in a read position resolves to
the declaration in the innermost enclosing scope that carries its name; an imported name resolves through its
binding, and the specifier is resolved the way the module graph resolves it — a **relative** path against the
file system, a **tsconfig** `baseUrl`/`paths` alias, or a package through `node_modules` — so a use in one
module reaches the declaration in the module it imports, whatever spelling the import used. `find`, `refs`,
`outline`, `unused` and `rename` answer from that
table — `rename` rewrites the declaration, the uses the scan resolved and the import binding in the modules
that import it, and has no compiler gate. JSX
component tags are references; a regular-expression literal and a string are not code.

Still missing, because it needs a type model rather than a tokeniser:

- no type model, so no type checking and no refactoring *in* the JS/TS — the lexical `rename` follows the
  token resolution, not a type model — a member reached through an
  untyped object, a computed member name and `eval` are outside the table; a specifier is followed when
  `tsconfig.json` (the nearest one, `baseUrl` and `paths`) or `node_modules` (`types`/`typings`/`module`/
  `main`, a subpath, an `index` file) answers it, and a runtime alias neither of them declares — a bundler
  plugin, an import map — is not guessed;
- no JSX/TSX component analysis, props/state checks or bundler configuration understanding;
- no DOM for HTML/CSS: `markup_files`/`analyze_markup` scan tags and apply HTML0001–HTML0015 and
  CSS0001–CSS0010 (section 1's table) — the CSS rules also run on every `<style>` block — and resolve a
  selector against the page index, compound parts (tag, classes, id, attribute conditions, the
  structural pseudo-classes) joined by descendant, child and sibling combinators. A selector whose
  meaning only a browser can decide is indexed but not reported; CSS0010 resolves specificity and source
  order for a declaration that is overridden on every element it matches, while equal specificity across
  two different files stays unknown, and CSS built in JavaScript is not resolved.

### 1.6 SQL, databases and EF Core

**Implemented** (2026-10-31): `sql_files` inventories the `.sql` files (lines, statements, verbs, tables
named) and `analyze_sql` applies SQL0001–SQL0012. The scanner masks `--`/`/* */` comments, single-quoted
literals (with `''` escapes), dollar-quoted bodies and bracket-quoted identifiers before it matches, so a
keyword inside a comment or a string does not fire; findings carry `file:line:column`, the source line and
a fix. It also reads the DDL the scanned files carry (`CREATE TABLE`, `ALTER TABLE … ADD`) into a schema
model and checks a statement's table and column names against it (SQL0011 a table the scan does not declare,
SQL0012 a column its declared table does not have), opt-out with `schema=off`.

What it deliberately does not do, because it is a pattern scanner and not a dialect parser: no reading of
a database, no formatting, no execution, no query-plan capture, no comparison of a schema against a live
catalog, and no language injection — SQL embedded in a C# string is reached by `trace_data_flow` as a sink,
but its text is not parsed or highlighted as SQL.

EF Core is visible only from the C# side: `DbContext`, `DbSet<>`, entity classes and configuration
classes are ordinary C# and get the full C# tool surface. **Implemented** (2026-10-30): `ef_migrations`
runs the project's own `dotnet ef` (list, add, script, diff, model-diff, remove, update, info,
dbcontext-list, pending-model-changes) and returns the command, exit code, stdout and stderr as structured
data; `model-diff` compares the `ModelSnapshot` with the entity classes property by property (properties
added in the model, shadow properties, type changes), so `pending-model-changes`'s boolean has a
field-level answer.

Still missing:

- inspection of the SQL a single query will generate (`action=script` returns the migration SQL,
  `action=diff` the SQL between two migrations; a query's own SQL needs `profile_database` on a running
  process);
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
  a validated manifest, port-forward sessions, exec, apply/delete/rollout with `confirm`) and `k8s_helm`
  wraps releases and `helm lint`; **`k8s_validate`** (2026-11-01) validates a manifest offline against a
  table of the built-in kinds (K8S001–K8S023: required fields, the selector/template agreement, a pinned
  image, a DNS-1123 name, a duplicate resource, a cluster-scoped kind with a namespace), and
  `server=true` sends the same text through `kubectl apply --dry-run=server`, so the OpenAPI schema and
  an admission webhook's rules are the cluster's own answer when one answers. **`k8s_port_forward`**
  starts, lists, inspects and stops a `kubectl port-forward` session. What remains outside is a cluster
  that does not answer (the offline table is the fallback) and a forward that outlives the server process;
- YAML schema validation for a CI pipeline file or a compose file: `yaml_files`/`analyze_yaml` read the
  structure (YAML0001–YAML0012), and a Kubernetes manifest gets the field-level validation above; no other
  YAML dialect has a schema here.

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
| WinForms | ⚠️ C# is analysed; `.Designer.cs` is generated code and several analyses skip it |
| WPF / WinUI 3 / MAUI / Avalonia / UWP | ⚠️ C# analysed; XAML by the markup layer (`xaml_pages`, `analyze_xaml`, `xaml_bindings`, `xaml_tree`, `xaml_navigate`, the verified edits) — the project's dialect is detected, a binding path is checked against the type the markup names, and a type the markup does not name is "not checked" rather than guessed (section 1.4) |
| gRPC | ⚠️ generated C# analysed; `.proto` files are not parsed |
| SignalR | ⚠️ hubs are C#; no hub-contract analysis. Runtime client tools (`signal_r_*`) exist to exercise a hub |
| Unity / Godot / MAUI workloads | ⚠️ whatever MSBuild can restore is loaded; no specialist project support |
| Legacy non-SDK projects | ⚠️ loaded through MSBuild; `update_framework` deliberately leaves `net4x`/`netstandard` alone |
| Solution/project creation | ✅ `project_template`: `dotnet new list` as data, and `create` runs a template (`classlib`, `console`, `web`, `xunit`, …) with a name, folder, framework and language, then reports the files it made — add the result to the solution with `add_projects_to_solution` |

---

## 2. Debugger

**Implemented** (2026-11-01): a Debug Adapter Protocol client over
[netcoredbg](https://github.com/Samsung/netcoredbg), installed on demand (`debug_install`), with 20
`debug_*` tools — the 19 below plus `debug_test`, which drives a test host under the same debugger —
the whole surface the adapter exposes over DAP, plus one Roslyn capability on top of it:

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
- **Exceptions**: `debug_exceptions` takes named exception types, evaluated by the server against the
  thrown type's **hierarchy** (resolved through the solution: a base class catches its derived types,
  `!Type` excludes a branch); `all` / `user-unhandled` say when to break when no type is named, and
  with no setting to change it returns what the process is stopped on — id, stage, type, message and
  the nested inner exceptions. A stop on an exception carries the same block automatically.
- **Inspection**: `debug_threads`, `debug_stack` (file:line per frame), `debug_variables`
  (Locals/Arguments/Autos, one level of expansion) and `debug_evaluate`, which also **writes**:
  `newValue` is the debugger's Set Value, so a breakpoint plus an assignment changes state and
  `debug_continue` resumes with it.

Sessions survive across tool calls, so a stop that happened between two calls is still there.

What the adapter does not expose, and what stands in for it:

- **Edit-and-continue and hot reload.** netcoredbg applies metadata deltas only over GDB/MI
  (`-apply-deltas`, `--hot-reload`), and its DAP mode — the one this server speaks — has no request for
  them; the .NET hot-reload agent is a separate protocol. The substitutes are `debug_restart rebuild=true`
  (edit → build → relaunch with the same breakpoints) and `debug_evaluate newValue` (change a value while
  stopped, then continue).
- **A memory watchpoint.** There is no `dataBreakpointInfo`/`setDataBreakpoints`, so
  `debug_data_breakpoints` breaks where the **source** writes the member — assignment, compound
  assignment, `++`/`--`, a `ref`/`out` argument or an initializer. A write from code the solution does
  not contain (a framework setter, reflection, a serialiser) does not stop, and a local variable is
  refused because it lives in one frame.
- **Hit and exception conditions are counted here, not by the adapter.** A stop that does not satisfy
  one is resumed transparently, so `debug_continue` can absorb several hits before it returns
  (`hitConditionSkips` says how many); the walk gives up after 500 absorbed hits and returns the stop it
  has. The stop is matched to its breakpoint by file and line, because the adapter reports no breakpoint
  id — a breakpoint the runtime moved to a different line is not counted. An exception type condition is
  matched against the thrown type's hierarchy; `exceptionTypeSkips` counts the stops absorbed the same
  way (500 maximum), and a type the model cannot resolve is matched by name only, which is also the
  fallback when no solution is loaded.

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

The practical consequence: a team can run the inspections, gate a change with `inspection_baseline`,
tune one rule and suppress one finding where the compiler reads it, add rules without building an
analyzer, apply the naming policy, and keep a warm analysis running while it edits. What remains is the
ReSharper proprietary catalogue itself: eight of its ids are implemented here, the map says which
capability answers ~30 more, and the rest would be a reimplementation of a closed-source product rather
than a gap a tool can close.

---

## 4. Decompiled code as a workspace

**Implemented** (2026-10-30): `assembly_workspace` decompiles an assembly (ILSpy) into a Roslyn
`AdhocWorkspace` — one document per type, the platform references and the assembly's own folder —
keyed on the module version id, and then navigates it: `load`, `list`, `search` (exact → prefix →
CamelHumps → contains), `source`, `il` (the same symbol as IL: the C#↔IL toggle), `references`
(Roslyn's symbol finder inside the decompiled set), `outline`, `unload`. Go-to-definition and
find-usages on code without sources are no longer impossible.

Still missing:

- rename inside the decompiled set (it would need a mapping back to IL, and an edit has no source to
  write);
- debugging the decompiled code (it needs the debugger plus a source-mapping step).

---

## 5. Context actions, templates and generation

ReSharper's C# layer ships **1176 actions** — 869 quick fixes attached to its ~2500 inspections and 307
context actions (the census is [resharper-context-actions.md](resharper-context-actions.md)). VisualMCP
has:

- **97 C# style ids** (`simplify_code`; the other 29 ids are delegated, not applicable to C#, or not a
  rewrite — each named with its category) and **15 VB ids**;
- **~800 inspection ids**; a fix exists only where the analyzer ships a `CodeFixProvider`
  (`apply_code_fix`);
- **54 context-action ids** through `context_action`: `import_type`, the argument guards
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
  `linq_method_to_query`, `convert_to_extension_block`), the fourth (`empty_string_to_string_empty`,
  `string_empty_to_empty_string`, `auto_property_to_explicit`, `insert_file_header`) and the fifth
  (`import_static_member`, `qualify_member`, `unqualify_member`, `wrap_in_quotes`, `split_string`,
  `join_string`, `check_span_index`, `block_body_to_expression`, `expression_body_to_block`);
- **templates** (`templates`): 18 live, 10 postfix (`.if`, `.null`, `.not`, `.var`, `.await`,
  `.foreach`, `.forr`, `.ret`, `.toList`, `.switch`) and 8 file templates (class, interface, record,
  struct, enum, static class, abstract class, xUnit test), plus the team's own in
  `.visualMCP/templates.json`; a file template creates the document behind the compiler gate;
- generators: `implement_members`, `create_from_usage`, `generate_docs`, `generate_members`,
  `implement_inpc`, `generate_tests` (whole type, or `memberName` for one member), and the two that
  closed here — `generate_dto_from_xml` (attributes and elements become properties, a repeated element a
  `List<T>`, an element with children a nested class, a leaf's type from its text) and
  `generate_comparer` (an `IComparer<T>` over the type's comparable properties);
- the test pair: `run_covering_tests` resolves the tests that reference a symbol and runs exactly them
  (the "run the covering test" action), and `find_regex_issues` compiles every constant pattern handed to
  `Regex` or a `[RegularExpression]` attribute with the real parser.

**Everything this table used to list is now a tool or an action**, and two things are left as boundaries
rather than gaps: **contract annotations** (`[NotNull]`/`[CanBeNull]` are the pre-nullable way of saying
what `string?` says today, so generating them would write code the language obsoleted) and **language
injections** (SQL, HTML or CSS inside a C# string is not a language this server hosts; `trace_data_flow`
still follows such a string to a SQL sink, and `analyze_sql` reads a `.sql` file — it is the *editor
language service for an embedded fragment* that is absent, and that is a second parser platform, not a
missing rewrite).

The split matters when comparing numbers with ReSharper: **97 style ids ≠ 54 context actions**, and
**~800 inspection ids ≠ 800 fixes**. Four families exist (style rewrites, analyzer code fixes, the
hand-written context-action ids, and the eight mechanical compiler fixes in `fix_diagnostics`). The
compiler family is the one that had **no** automatic fix until 2026-10-30; it now covers the shapes that
need no judgement — CS0168/CS0219 unused local, CS0169 unused field, CS0067 unused event, CS0105
duplicated using, CS0108/CS0114 hidden member, CS0109 redundant `new` — each accepted only when the
project compiles no worse, and every other `CS####` still has no fix here.

---

## 6. Refactoring gaps

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

---

## 7. Architecture, diagrams and exploration

The numbers and the diagram data are both here:

- **Graphs as artifacts**: `export_dependency_graph` at project, namespace and **type** level
  (`analyze_dependencies`, `analyze_circular_namespace_dependencies`, `analyze_namespace_coupling` and
  `check_layer_violations` remain the numbers); `call_graph` (incoming/outgoing, a depth, Mermaid/DOT);
  `sequence_diagram` (static call order); `control_flow_graph` (Roslyn basic blocks, branch conditions and
  try regions).
- **Value tracking**: `track_value` lists the definitions (writes, including the declaration) and the uses
  (reads) of a symbol under a position, each with file:line, member and snippet.
- **File structure**: `reference_index action=outline` (a file's declarations) and
  `get_type_members`. Recent files/edits: `edit_history` and `command_log`.
- **Localization analysis**: `localization_report` — unused `.resx` keys, missing translations per culture,
  extra keys, and user-facing literals as candidates. Spelling: `find_spelling_issues`.
- **Inventories as data**: `assembly_workspace`/`inspect_assembly` for an assembly,
  `list_nuget_packages`/`check_nuget_updates` for its packages, `solution_map`/`read_tree` for the
  solution's structure.

Implemented 2026-10-30: the **endpoint inventory** (`find_endpoints`), the **TODO list**
(`find_todos`), **go to file** by fuzzy/CamelHumps name (`find_file`), **stack-trace analysis**
(`analyze_stack_trace`) and the codebase map (`solution_map`).

`plan_feature_change` produces an ordered text plan with impact counts; `impact` counts plus
`export_dependency_graph` are the data behind it.

---

## 8. Build, run and packaging

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
as data; `nuget_sources` manages the package sources and their credentials through `dotnet nuget`.

**Everything this section listed as missing now exists**, and each one is a tool rather than a claim:

- **`project_template`** — a project, not just a file in one: `dotnet new list` as data and `create` for
  one template with its name, folder, framework and language, reporting the files it made.
- **`installer_project`** — `kind=wix` scaffolds a WiX v4/v5 project (an MSI that packs the publish
  folder, with a `MajorUpgrade` and a stable `UpgradeCode`), `kind=clickonce` writes a ClickOnce publish
  profile; `action=verify` really builds what was scaffolded. MSIX is not generated: it needs the Windows
  packaging tools, not an SDK.
- **`deploy_target`** — IIS through a Web Deploy publish profile (`dotnet publish -p:PublishProfile`,
  which drives `msdeploy`), an Azure Web App (`az webapp deploy`) and an Azure Static Web App
  (`az staticwebapp deploy`); every call needs `confirm=true`, and a missing `msdeploy`/`az` is a
  structured answer.
- **`--cli`** — the standalone command line a pipeline drives without an MCP client:
  `VisualMCP --cli inspect|duplicates|cleanup` prints findings (text, or `--json`) and returns the exit
  code the ReSharper command-line tools use (0 pass, 1 gate failed, 2 usage/load error).
- **`migration_campaign`** — a feature-level migration with the compiler as the gate: `nullable`,
  `file-scoped-namespaces`, `braces` and `primary-constructors` across a solution or a project, gated per
  project and isolated per file when that project stops compiling, with a diff and a refusal reason per
  file.
- **`http_file`** — a `.http`/`.rest` file as data: the variables (with their lines), the requests and
  eight rules (`HTTP0001`–`HTTP0007`), plus `append` and `set-variable` edits. It reads the same parser
  `run_http_file` executes, so what it validates is what will be sent.

---

## 9. How this inventory is maintained

- The ✅ side lives in [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md). This page is the
  ❌/⚠️ side and the reasons behind them.
- When a gap closes, delete its row here and add the tool to the coverage table; do not leave a "was
  missing" note.
- When a new tool ships, check whether it removes a row here — the two pages are meant to sum to the same
  product, not to be two independent claims.

---

## 10. Revision log

The batches that closed rows on this page, newest first. A count is the live inventory at the end of the
batch (`capability_report` prints the current one); the per-feature record of each batch is in
[CAPABILITY_MAP.md](../CAPABILITY_MAP.md).

| # | Date | What closed | Tools |
|---:|---|---|---|
| 46 | 2026-11-01 | **§5 empties: the proprietary context-action tail and the four missing families become tools.** Nine context actions close the import, wrap, literal/string, guard and statement families: `import_static_member` (resolves the type and inserts `using static`), `qualify_member` (the fully-qualified name, because a `using static` imports the members and not the type name), `unqualify_member` (drops the qualifier and imports it), `wrap_in_quotes`, `split_string`/`join_string`, `check_span_index` (a bounds guard before the statement that indexes a span or an array), `block_body_to_expression`/`expression_body_to_block` — 54 ids now. Three tools close the rest: `generate_dto_from_xml` (attributes and elements become properties, a repeated element a `List<T>`, an element with children a nested class, a leaf's type inferred from its text), `generate_comparer` (an `IComparer<T>` over the comparable properties, its accessibility following the type's), `find_regex_issues` (every constant pattern handed to `Regex` or `[RegularExpression]` compiled by the real parser — `IsMatch` takes the input first, so the argument is chosen by parameter name, not position). `generate_tests` gains `memberName` (a test for one member) and `run_covering_tests` composes the covering-test lookup with the run. Two boundaries remain and are stated: contract annotations (`[NotNull]`/`[CanBeNull]` are what `string?` replaced) and language injections (an embedded SQL/HTML fragment's editor service is a second parser platform). 24 new tests across three suites; 54/54 in the context-action and generation suites | 425 → 429 |
| 45 | 2026-11-01 | **§8 empties: the last five items of build/run/packaging become tools, and §1.9 gains a project scaffolder.** `http_file` reads a `.http`/`.rest` file as data — variables with their lines, requests, and eight rules (HTTP0001 a block with no request line, HTTP0002 an unknown method, HTTP0003 an undeclared `{{placeholder}}`, HTTP0004 a variable declared twice, HTTP0005 two requests with the same name, HTTP0006 a line that is neither header nor body, HTTP0007 no request at all) — plus `append` and `set-variable`; `run_http_file` now parses through the same `HttpFileParser`, so validation and execution cannot disagree. `migration_campaign` applies one named rewrite (`nullable`, `file-scoped-namespaces`, `braces`, `primary-constructors`) across a solution or project, compiling each project before writing and isolating the per-file refusals inside a project that stops compiling. `project_template` wraps `dotnet new` (list as data, create with name/folder/framework/language and the files it made). `installer_project` scaffolds a WiX v4/v5 project (an MSI over the publish folder, `MajorUpgrade`, a stable `UpgradeCode`) or a ClickOnce publish profile, and `verify` really builds it. `deploy_target` deploys to IIS through a Web Deploy publish profile, an Azure Web App or a Static Web App, behind `confirm=true`, with a missing `msdeploy`/`az` as a structured answer. `--cli inspect\|duplicates\|cleanup` is the standalone command line a pipeline drives without an MCP client, with the exit-code contract of `inspectcode`/`dupfinder`/`cleanupcode`. §5 closes four more context-action ids (`empty_string_to_string_empty`, `string_empty_to_empty_string`, `auto_property_to_explicit`, `insert_file_header` — the last reads the `.editorconfig` `file_header_template`). 35 tests across five new suites, all green | 420 → 425 |
| 44 | 2026-11-01 | **The inventory loses the sections that were not gaps**: "Boundaries that are not gaps", "Beyond ReSharper" (the residuals of the families SonarQube/CodeScene/the IDEs also cover) and "What to do today" (the workaround table, every row already closed) leave the page. Their content moved to the coverage page's new **Cross-cutting boundaries** section — the properties every result shares (the gate, scanners, generated code, reflection, referenced assemblies, Razor, metacode readers, profiling, automation/safety) and the residual boundary of taint, mutation, database profiling and churn — so this page now holds only open capability rows, and the plan stays on the board. Two stale claims fixed on the way: §8 said containers and Kubernetes had no deployment target (they are `docker_*` / `k8s_*` plus `dotnet_publish`, with `add_ci_workflow` for the pipeline), and CAPABILITY_MAP still called taint analysis and `get_quality_trend` "not closable" although both shipped. Remaining open rows: the §1 scanner boundaries, the §5 proprietary context-action tail, and §8's installer authoring, `.http` authoring/schema, IIS/Azure deployment, standalone CLI and feature-level campaigns | 420 (unchanged) |
| 43 | 2026-11-01 | **The limits page stops being a second inventory.** Every boundary it listed now lives where it is checked against this build — the properties of the analysis, the Razor scanner, the metacode readers, profiling and the safety facts later moved to the [cross-cutting table](resharper-codemaid-coverage.md#cross-cutting-boundaries), the debugger adapter's own surface in §2, the language map in §1 — and `docs/reference/limits.md` states only requirements, what a result proves, the scope of the product and the license, with links to those sections. Nothing was dropped: the old page also claimed Java and Rust had no frontend, which stopped being true when `JavaFrontend`/`RustFrontend` shipped, and that stale row is gone with the table. Code in the same change: duplicate ids now follow a layout chosen in `_ViewStart.cshtml` — the nearest file to the page, and not applied to the layout or a partial, which select their own — through `RazorPartialResolver.ViewStartFor`; the test builds a page with two `_ViewStart` files and asserts the nearest layout is the one compared. 67/67 in the Razor suite | 420 (unchanged) |
| 42 | 2026-11-01 | **The install needs nothing but the .NET SDK**: the POSIX routing guard and the installer's configuration edit are now modes of the published binary — `VisualMCP --hook pre-tool-use` answers the `PreToolUse` payload on stdin, `VisualMCP --config <mode> …` edits a client's JSON (server entry, hook matcher, AGENTS.md block) — so `hooks/pre-tool-use.py` and the Python program embedded in `scripts/install.sh` are gone, and `install.sh` no longer looks for an interpreter: it registers the guard unconditionally, and before using either mode it checks the resolved binary answers `--config` (a build from before this change would start the server instead). Both modes are ported rule for rule from the Python guard and covered by 32 tests (the decisions, the exact three-key payload, the JSON shapes, a foreign hook kept, an old Python hook replaced, the AGENTS block written once and removed). Same change: `SNITCH/.gitignore`'s exception for the C# source folder `Languages/**/Packages` still named the pre-`Implementations` path, so six NuGet service files were untracked and a clean clone did not compile — the exception follows the tree now and the files are back in git. The Windows guard stays `hooks/pre-tool-use.ps1` (PowerShell ships with the OS) | 420 (unchanged) |
| 41 | 2026-11-01 | **The JavaScript/TypeScript module-resolution boundary of §10 narrows to what a static reader can prove**: one resolver (`JavaScriptPaths.Resolve`) now answers a specifier the way the project's tools do — a relative path (with the compiled-to-source extension and a directory `index`), a **`tsconfig.json`** `compilerOptions.baseUrl`/`paths` alias (the nearest config, read as TypeScript reads its own JSON: comments and a trailing comma allowed, one wildcard in pattern and target, the first existing target wins), or a **package** through the `node_modules` folders above the file (`types`/`typings`/`module`/`main`, a subpath, a scoped name, a directory index). `js_files` builds the module graph from it, so an alias-based application is no longer a set of unrelated files: an edge links two **scanned** scripts, a resolved dependency outside the scan is counted per file as `externalImports`, and `resolvedImports` counts every specifier that points at a file; JS0017 stays relative-only, because a package nothing installed is not a defect a scanner can prove. `js_symbols` resolves through the same function, so `refs` crosses a `@/…` import and `outline` reports a package import as resolved. What stays outside: a runtime alias neither `tsconfig.json` nor `node_modules` declares (a bundler plugin, an import map), a package `exports` condition map, and the type model itself. Three tests — the alias edge, a package counted and not walked, and the alias/package paths through `js_symbols`; 39/39 in the JavaScript suite | 420 (unchanged) |
| 40 | 2026-11-01 | **The last VB refactoring paths close §10's VB row**: `generify` (a method or a type — the concrete type becomes `(Of T)`, constraints are written in VB's `As { … }` vocabulary, and a type parameter that cannot be inferred from the parameters is supplied as an explicit `(Of Concrete)` at every call site or reference; a self-referential type and an existing type-parameter list are refused), `global_usings` (every file's plain `Imports` becomes a project-level `<Import Include="…" />` item and the statement goes; the gate compiles the edited project with the new global imports applied to its options, then the solution reloads) and `transform_parameters` completes with `wrap` (a parameter object with a constructor and a property per parameter, call sites building it) and `replace-with-property` (a property on the receiver, the declaration and body rewritten, an assignment inserted before each call; a Shared method, a lambda call and a named-argument call are refused). Seven tests (4 generify, 2 new transform, 1 global usings); 10/10 across the three classes | 420 (unchanged) |
| 39 | 2026-11-01 | **The CSS boundary of §10 is closed at the cascade**: `analyze_markup` gains **CSS0010** — every rule's declarations and its specificity are parsed (ids, then classes/attributes/pseudo-classes, then tags/pseudo-elements), the rule is matched against each page's element index, and a declaration that is overridden on **every** element it matches is reported (higher specificity, `!important`, or the same specificity and a later rule in the same file; equal specificity across two different files is treated as unknown because the link order is not in the scan). The rule/declaration parser is exposed as `MarkupAnalyzer.CssRules`, and `MarkupAnalysis` carries the rules. Three tests — the parser and specificity, the dead declaration, and a declaration that wins on some element and is not reported — 31/31 in the markup suite | 420 (unchanged) |
| 38 | 2026-11-01 | **Three more §10 boundaries narrow**: `js_symbols action=rename` and `fsharp_symbols action=rename` rename one declaration and every reference the lexical table resolved — for JavaScript the uses in the importing modules and the import binding itself, for F# the uses before it in its file and in a file that opens the declaring module — previewed by default and written with `dryRun=false`, with no compiler gate because neither language has one here. `k8s_validate server=true` also sends the same manifest through `kubectl apply --dry-run=server`, so a CRD's OpenAPI schema and an admission webhook are checked by the cluster instead of the built-in table (a missing kubectl or an unreachable cluster is reported). What stays a boundary: the JS/TS type model and its other refactorings, F# type checking, and CSS specificity/cascade. Three tests, one per path; 17/17 in the symbol-table suite | 420 (unchanged) |
| 37 | 2026-11-01 | **The Visual Basic refactoring row of §10 narrows to what VB cannot express**: thirteen paths that used to stop at a C# syntax tree now run on a `.vb` document under the same compiler gate — the `introduce_*` family (a `Dim` with the inferred type and every occurrence rewritten, a `Private ReadOnly` field for a context-free expression, a compile-time parameter passed at every parenthesised call, a substring into a `Private Const`), `invert_boolean` (every `Return` negated, optional rename), `copy_type` (`Sub New` needs no rename), `static_to_extension`/`extension_to_static` (VB's `<Extension>` attribute and the call-site receiver), `convert_anonymous_to_type` (`New With {.A = 1}` into a `Friend NotInheritable Class`), `implement_inpc` (the `Implements` clause, the event, a caller-name helper and one backing-field property per auto-property), `pull_members_up` (an interface signature plus `Implements I.Member` on every member, or a move into the base class), `push_members_down` (into the single derived type that uses the member; refused when anything else does) and `transform_parameters` for `make-optional`/`make-required`/`change-type`. What stays C#-only is what VB cannot express — records, primary constructors, nullable reference types — plus `global_usings`, `generify` and the two call-site rewrites of `transform_parameters`. Twenty-two tests; 293/293 in the focused refactoring/VB suite | 420 (unchanged) |
| 36 | 2026-11-01 | **The Visual Basic declaration-shape tail is closed** — the last boundary of the workaround table that needed only a parser. Eleven refactorings that used to stop at a C# syntax tree now run on a `.vb` document under the same compiler gate, as a partial of `VbRefactoringTail` (text spans from the VB semantic model, the projection compiled before anything is written): **`inline_symbol`** (a local with one initializer, or a `Function`/`Property` whose body is a single `Return`, parameters substituted at each call site; a write or a `ByRef` argument refuses), **`inline_parameter`** (every call site must pass the same value, which may not read a local, a parameter, `Me` or an instance member), **`inline_class`** (the helper's members moved into the owner, `field.Member` losing its prefix, every other use of the helper a refusal), **`move_static_members`** (a `Shared` member, both the qualified and the bare reference re-pointed, `Imports` added where the target namespace is not visible), **`move_instance_method`** (a `Shared` method whose first parameter is the receiver — VB writes `source As SourceType`, not `SourceType source` — with `Me` and the implicit instance members rewritten, `MyBase` and Private/Protected state refusing), **`move_type_to_outer_scope`**/**`move_type_to_nested_scope`** (the declaration moves scope, references are qualified or unqualified accordingly, `Imports` added, the emptied file deleted), **`extract_class`** (a `Friend NotInheritable Class` beside the owner, a `Private ReadOnly` holder plus one forwarding `Sub`/`Function`/`Property` per selected member), **`extract_superclass`** (the base in its own file, `Inherits` added as VB's single base clause), and **`function_to_indexer`**/**`indexer_to_function`** (VB's `Default ReadOnly/WriteOnly Property` with `Get`/`Set`, call sites rewritten — `x.Get(i)` ↔ `x(i)` — and a keyword name bracketed as `[Get]`). The selection checks are the C# ones in VB's terms: a member that travels may only read members that travel with it, a field cannot be forwarded, and a constructor that stays behind may not assign one. Fourteen tests, one per operation plus two refusals; 296 refactoring/VB tests green. What stays C#-only in the catalogue is the refactoring whose output is a C# construct (records, primary constructors, anonymous types, generic parameters, nullable annotations, INPC, usings, extension methods, pull/push, `transform_parameters`, `introduce_*`) | 420 (unchanged) |
| 35 | 2026-11-01 | **The test workflow leaves this page**, after the two rows that did not hold were made true. `run_tests`'s `projectFilter` selected **test names** (`--filter FullyQualifiedName~…`) while it was documented as a project filter; it now resolves the solution's test projects (a `.csproj` referencing `Microsoft.NET.Test.Sdk`), runs each matching project, refuses a filter that matches none by naming the projects that exist, and no longer invents a `--filter`. Both `run_tests` and `run_test` now write `.visualMCP/last-test-run.json`, so `rerun_failed` really does re-run the failures of either — one record per project, cleared by a green run, the first single-project shape still read. `get_test_coverage_map`'s `projectName` had the same defect and now runs that project's `.csproj` (and no longer needs a prior `--no-build`). Everything else in the section already held: discovery with the inline data rows, coverage (line and branch), debugging a test, continuous testing, affected tests, flaky detection, mutation testing, scaffolding. The section is deleted and its features are the [Testing](resharper-codemaid-coverage.md#testing) table | 420 (unchanged) |
| 34 | 2026-11-01 | **The four remaining implementable §10 boundaries**: **`js_symbols`** — a lexical symbol table for JavaScript/TypeScript/JSX/TSX: a tokeniser over the masked source, one scope per brace block, a declaration for every name a declaration position introduces (function, class and its members, declarator incl. destructuring, parameter, TypeScript interface/type/enum, import binding) and a resolution to the innermost scope carrying the name; an imported name resolves through its binding to the exporting module when the specifier is relative, JSX component tags are references, and `outline`/`find`/`refs`/`unused` answer from it. **`fsharp_symbols`** — the same four questions over F#, as a name table: modules, namespaces, types, union cases, record fields, `let` bindings and members, a name resolving before it in its file and, across files, only through an `open`. **`k8s_validate`** — an offline manifest validator for the built-in kinds (K8S001–K8S023: required fields, selector/template agreement, a pinned image, a DNS-1123 name, a duplicate resource, a cluster-scoped kind with a namespace, an empty ConfigMap/Secret, a dangling volume reference), with its own YAML path reader. **`k8s_port_forward`** — `kubectl port-forward` as a session: start (waits for the forwarding line), list, status, stop. Also corrected the stale rows this page still carried: the VB row said 15 style ids (41 now) and no VB context actions (33 ids), and the EF section still called the model-vs-snapshot field diff missing. What stays a boundary: a type model for JS/TS (no rename/refactoring), type checking for F#, a live database, the OpenAPI schema and a forward outliving the server, CSS specificity and the cascade, the VB declaration-shape tail, and the by-design boundary (§4) | 416 → 420 |
| 33 | 2026-11-01 | **Four §10 boundaries narrowed, from the model instead of a guess**: `analyze_sql` reads the DDL the scanned files carry (`CREATE TABLE`, `ALTER TABLE … ADD`) into a schema model and gains **SQL0011** (a table the scan does not declare) and **SQL0012** (a column its declared table does not have), `schema=off` to turn it off; `ef_migrations action=model-diff` compares the `ModelSnapshot` with the entity classes **property by property** (added in the model, shadow properties, type changes), so `pending-model-changes`'s boolean has a field-level answer; a binding's data context is also read from a root `<Root.DataContext><local:ViewModel/></Root.DataContext>` property element; `trace_data_flow` keys a field by its declaration **and its receiver**, so `a.Value` and `b.Value` are different entries and only `this`/field/property receivers keep an identity across methods. The Visual Basic declaration-shape tail also shrinks by three: `inline_field`, `move_to_namespace` and `property_to_function` now run on a `.vb` document under the same compiler gate. The remaining §10 boundaries are the ones that need a parser rather than a model: JS/TS symbols (§1.5), F# semantics (§1.3), a live database (§1.6), port-forward and manifest schema validation (§1.7), the rest of the VB tail, and the by-design boundary (§4) | 416 (unchanged) |
| 32 | 2026-11-01 | **The Visual Basic refactoring tail**: six refactorings that used to stop at a C# syntax tree now run on a `.vb` document under the same compiler gate — `extract_interface` (an `Interface` in VB's vocabulary, plus the `Implements` clause VB requires on the class *and* on each member), `extract_method` (a contiguous block into a private `Sub`/`Function`, the locals and parameters it reads becoming its parameters, refused on a jump or a write), `move_type` (the declaration into a file of its own, carrying the file's `Imports` and its `Namespace`), `property_to_auto` (a trivial `Get`/`Set` pair into an auto-`Property`, dropped backing field; refused when the field is read elsewhere), `function_to_property` (a parameterless `Function` into a `ReadOnly Property`, call sites in both VB spellings, `GetCount()` and `GetCount`) and `move_to_folder`, whose folder derivation now reads a VB `Namespace` block. Seven tests, one per path and one refusal | 416 (unchanged) |
| 31 | 2026-11-01 | **§2 Debugger, closed against the adapter's real surface**: the DAP requests netcoredbg does implement are now all exposed — **per-exception-type filters** (`filterOptions`: full type name, `!Type` to exclude) and the stopped exception's own block (`exceptionInfo`, returned by `debug_exceptions` and by every stop on an exception), **function breakpoints** (`setFunctionBreakpoints`), **Set Value** (`setExpression` through `debug_evaluate newValue`), and **hit conditions counted here** because the adapter ignores them. Two features the adapter has no DAP request for are delivered anyway: **data breakpoints** as the solution's Roslyn-resolved write sites (`debug_data_breakpoints`) and **restart with a rebuild** (`debug_restart`) as the edit → build → run loop. Only edit-and-continue/hot reload stays out — the adapter applies metadata deltas over GDB/MI only — and it is recorded in [reference/limits.md](reference/limits.md#debugger), not here | 413 → 416 |
| 30 | 2026-11-01 | **Four §10 boundaries narrowed**: `assembly_workspace action=il` returns the same decompiled symbol as IL, so the C# and IL views are one call apart; `k8s_kustomize` renders an overlay offline and `k8s_exec` runs one confirmed command inside a pod (port-forward stays out); `ef_migrations action=diff` scripts the SQL between two migrations; `analyze_markup`/CSS0009 resolve compound selectors — descendant/child/sibling combinators, attribute conditions and the structural pseudo-classes — against a per-page element index, and a selector with a dynamic pseudo-class is indexed but never reported. Specificity, the cascade, a model-vs-snapshot field diff and port-forward remain outside | 411 → 413 |
| 29 | 2026-11-01 | **Three limits closed by extending the tool that named them**: `trace_data_flow` follows a field written in one method and read in another **on any object** (before, only a field of the reading type), and the answer states the instance-insensitivity that makes it an over-approximation; `analyze_razor_pages`/`find_razor_vulnerabilities` resolve the partials a form renders (`<partial name=…>`, `@Html.Partial("…")`, the `RenderPartial` family, nested with a cycle guard), so RZ1005 no longer fires on a form whose token lives in a shared partial; `change_signature` gains a **VB path** — the declaration and every plain call site, call parentheses optional, `Optional` emitted for an added default, refused on an `AddressOf` use | 411 (unchanged) |
| 28 | 2026-10-31 | **The Visual Basic tail**: `simplify_vb_code` rewrites 24 more VB ids (the qualifier, the `If`/`Else` folds, a completed `Select Case`, object initializers, tuple element names, `NameOf`, interpolation, both LINQ chains, `HashCode.Combine`, modifier order, accessibility, `ReadOnly`, an unused member, an unread field, unreachable code, a dead store, an unused parameter, an auto-property and the three layout rules) — 41 of the 49, with 8 refused by id and the reason; `change_type`, `use_base_type`, `type_to_partial` and `safe_delete` gain a VB path, so seven refactorings now run on a `.vb` document. The C#-syntax members of the refactoring catalogue keep needing their own path | 404 (unchanged) |
| 27 | 2026-10-31 | **The HTML/CSS limit narrows**: `markup_files` indexes every page's tag/class/id set and every stylesheet's simple selectors; `analyze_markup` gains **CSS0009** (a `tag`/`.class`/`#id`/`tag.class` selector that matches no element in the scanned pages) and **HTML0015** (a class used in markup that no scanned stylesheet defines), and the CSS rules and selectors now run on every `<style>` block with the finding reported on the page's line. The match is lexical and set-against-set: combinators, attribute selectors, pseudo-classes, specificity and the cascade stay out, a CDN stylesheet or CSS-in-JS is outside the scan | 404 (unchanged) |
| 26 | 2026-10-31 | **Three boundaries narrowed**: `list_generated_sources includeSource=true` returns the text of the trees a source generator emits (before, only their metadata); `js_files` builds the module graph from the relative specifiers and reports its import cycles (before, no graph); `ef_migrations action=pending-model-changes` answers whether the model still matches the migration snapshot | 404 (unchanged) |
| 25 | 2026-10-31 | **The depth of the scanned layers and the data-flow reach**: `analyze_yaml` gains YAML0009–YAML0012 (kind without apiVersion or the reverse, metadata with no name, a container with no image, two containers with the same name); `analyze_js` resolves a relative import against the file system and reports JS0017; `trace_data_flow` follows a value stored in a field of the same type, across the method that writes it and the method that reads it | 401 (unchanged) |
| 24 | 2026-10-31 | **Visual Basic, the last two C#-shaped surfaces.** `context_action` routes a `.vb` document to a VB engine of 33 ids (guards, wrapping, Imports, interpolation, digit separators, named arguments, var/explicit type, split declaration, StringBuilder, merge/split checks, conditional access, `Select Case`, LINQ both ways, For Each↔For, invert loop) and refuses the C#-only ones with the reason; `apply_code_fix` selects analyzers **and fixers** by language, so the .NET analyzers' 100+ VB `CodeFixProvider`s run on a `.vb` document (CA1822 → `Shared`) | 401 (unchanged) |
| 23 | 2026-10-31 | **Section 9 leaves this page.** Version control and collaboration is now a feature section of [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md): the 28 Git tools (27 `git_*` and `commit_lint`), the 8 GitHub tools and `code_review`, 37 in all | 401 (unchanged) |
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
