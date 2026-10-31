[Docs home](../README.md) › **Limits**

# Limits

A tool is only trustworthy if it is clear about what it cannot do. This page is the honest list.

## Platform

- **The .NET 10 SDK is required.** The server targets `net10.0` and loads solutions through
  `MSBuildWorkspace`.
- **The routing guard needs Python 3 on Linux/macOS.** Its hook reads a JSON payload, so the POSIX hook
  is a small Python script; the installer registers it only when an interpreter is found and says so
  when one is not (the server install is unaffected). The Windows hook is PowerShell and needs nothing
  extra.
- **A solution must evaluate and restore.** If MSBuild cannot load a project — a missing SDK, a broken
  `Directory.Build.props`, a package feed that is unreachable — the semantic model degrades to MSBuild
  diagnostics. It does not silently fall back to text search.

## Static analysis

- **The compiler gate proves that a change compiles, never that it is correct.** Behaviour is measured by
  `verify_change`, which compares two runs test by test; code the suite does not cover comes back
  `unverified` instead of being called safe.
- **Heuristic rules are finders, not proofs.** `analyze_complexity`, the Razor rules and security scanning
  report patterns with their confidence; a green run means "no pattern matched", and `inspection_baseline`
  is what makes that a statement about a change rather than about a moment.
- **Source generators are outside the model.** `list_generated_sources` names the generators a project
  references and the trees they emit, and with `includeSource=true` returns their text; the trees are not
  documents, so no symbol navigation reaches them.
- **Reflection and framework conventions are invisible to the reference graph.** `find_reflection_usage`
  resolves the reflective lookups whose name matches a declaration in the solution, and
  `framework_entry_points` classifies what a framework calls by convention; a name neither tool resolves
  still looks dead.
- **A referenced assembly is metadata, not analysed source.** `find_external_usages` reports the surface
  the solution actually uses, with call sites; the internals stay outside.

## Razor and Blazor

- The scanner is **heuristic and per file**. It does not follow values across partials, view models or
  JavaScript.
- **Markup inside `@{ … }` and `@code { … }` is not analysed** — that is C#.
- Anti-forgery tokens and duplicate ids are checked **per file**; a token in a partial rendered inside
  the form can still be reported.
- **Dynamic values are not guessed.** A rule whose answer depends on a runtime value stays silent.
- Files over **2 MB** and unreadable folders are reported as skipped, not analysed.
- `.cshtml`/`.razor` are not Roslyn documents. Tools that require the semantic model do not see them;
  `razor_*` reads them from disk instead.

## Metacode and decompilation

- **The pseudocode model is a syntax model, not a language service.** Python and C++ are read from their
  AST dumps by `translate_python_file` and `translate_cpp_file`, which produce the dump when the
  interpreter or compiler is installed (or take one you supply): no type checking, no symbol navigation,
  no editing. Java, Go and Rust have labels in the model, not frontends.
- **Unsupported constructs stay visible** as `RAW[lang] … // reason` plus a diagnostic. A translation can
  therefore be incomplete; the diagnostics say where.
- **Ghidra is an external dependency** with a real runtime cost. `analyze_native_binary` can take tens of
  seconds, is bounded by a timeout, and needs a JDK 21+. Ghidra is downloaded once when no installation
  is found (a few hundred MB); set `GHIDRA_INSTALL_DIR` to use a copy that is already there.
- ILSpy reconstructs C# from IL. Names can be compiler-generated, and modern language features may render
  as lower-level constructs.

## Profiling

- The target must expose a **diagnostics IPC endpoint**. A process with diagnostics disabled cannot be
  profiled.
- Sampling is statistical. Short captures are noisy; the verdict tools refuse windows they cannot judge
  (`profile_review_change` rejects windows under 500 ms, `profile_threadpool` can answer
  `inconclusive`).
- **Allocation sites need stack walks.** Use `profile=gc` or `diagnose`; a CPU-only trace cannot attribute
  allocations to lines.
- **`file:line` needs PDBs.** Without them you get method and module names. `profile_symbols` can fetch
  them from a symbol server.
- **ClrMD cannot attach to the server's own process.** Profile the application, not VisualMCP.
- Inlining, async state machines and framework frames can hide the user code; the hot-path and I/O tools
  exist to surface it, but they are still reading a sampled stack.
- **`profile_async` and `profile_database` need their presets.** These are EventSource providers, not CLR
  runtime keywords: collect with `profile=async` / `profile=database`. An empty answer names the providers
  the trace did contain and the command that fixes it; `profile_events` shows any provider's schema.
- **`profile_root_cause` needs both halves.** PDBs for the measured side, a readable source file (C#,
  Visual Basic or Razor under the solution) for the AST side. It translates the file from disk, so a
  stale persisted index does not matter; a missing half is reported, never guessed.
- **Instrumentation changes the binary.** `profile_instrumentation action=instrument` writes a rewritten
  copy (never the original), which must run with `Snitch.Profiling.Probe.dll` beside it, and every call
  costs two events. Methods whose IL cannot be wrapped safely are skipped and named; a strong-named
  .NET Framework assembly may refuse the rewritten copy.

## Automation and safety

- **`run_command` is not sandboxed.** It runs with the privileges of the server process.
- **`run_plan` can call any tool and any shell command.** Its validation prevents broken plans, not
  dangerous ones.
- **`confirm=true` is a guardrail, not authorization.** It prevents accidental destructive calls; it does
  not add a permission system.
- **The edit history is per solution and per file.** It records what this server wrote, not what an IDE or
  a script wrote.
- **The store is a cache.** Indexes are snapshots keyed on content hashes; after large external changes,
  rebuild them or reload the solution.

## Language and platform coverage

The semantic engine is Roslyn, and it reads both languages a .NET solution can be written in: C# and
Visual Basic. Everything else is a dedicated scanner (Razor/Blazor, SQL), a markup layer (XAML) or text
indexing, and the non-.NET toolchains are wrapped rather than reimplemented. This is the whole map:

| Language / platform | Coverage |
|---|---|
| **C#** | The full surface: navigation, references, refactorings, style, inspections, complexity, profiling, decompilation |
| **Visual Basic** | The **symbol index** (`find_symbol`-style queries over `reference_index`: declarations, uses by kind, hierarchy, outline, metacode, unused); **metacode** (translate, compare, index trees and complexity, generate); `structural_search` with VB patterns (`language=auto|cs|vb`); `format_code`, `optimize_usings` (for `Imports`) and `code_cleanup`; **inspections** through the packs that ship VB analyzers (the .NET analyzers' `vb` set and the VMC0001 house rule — the C#-only packs say they do not cover the project) and **their code fixes** through `apply_code_fix` (the 100+ VB `CodeFixProvider`s in the .NET analyzers' `vb` assembly); **analysis** (`find_dead_code`, `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns`, `complexity_report`, `get_complexity_hotspots`, `get_metrics`, `get_cohesion_metrics`, `find_duplicate_code`, `analyze_complexity`); **context actions** (`context_action` routes a `.vb` document to 33 of its 41 ids — the guards, Try/If/Using wrapping, `Imports`, interpolation, digit separators, named arguments, var/explicit type, split declaration, StringBuilder, merge/split checks, conditional access, `Select Case`, LINQ both ways, For Each↔For, invert loop; the C#-syntax ids are refused with the reason); **refactorings** `apply_rename`, `encapsulate_field`, `add_params`/`remove_params`; `simplify_vb_code` (15 ids, 34 refused with a reason); `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `run_code_analysis`, `text_index`. The refactorings that are C# syntax or C# idiom refuse with that reason. A solution with **no C# project** is a supported workspace: `check_solution` reports its Visual Basic projects and names the tools that apply. A runnable sample is [samples/VbDemo](../../samples/VbDemo/README.md) |
| **Razor / Blazor** | A heuristic markup scanner (28 rules) and verified edits. It is not a Razor compiler: `@model` members, tag helpers, component parameters, cascading values, `@bind` types and CSS isolation are not type-checked |
| **F#** | `fsharp_files` lists the declarations (modules, types, top-level functions, opens) and `analyze_fsharp` applies FS0001–FS0010 over masked source; `build_project` returns the F# compiler's own `FS####` diagnostics and `run_tests` runs an F# test project. There is no semantic model (F# is not a Roslyn language), so no symbols, references, refactorings or inspections — the scanner resolves nothing |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | A heuristic markup layer: inventory, nine rules (`x:Name`/`x:Key` duplicates, `x:Class`, event handlers, resource keys, `DataTemplate`, name collisions, XML) and navigation (`x:Name` ↔ field, key ↔ uses, `BasedOn` chain). The XAML is not compiled, so bindings, tag helpers and styles are not type-checked; no designer, no XAML refactorings. The C# code-behind is fully analysed |
| **JavaScript / TypeScript** | The toolchain, through the project's own npm/pnpm/yarn: `node_info`, `node_install`, `node_script`, `node_typecheck` (tsc → `TSxxxx` diagnostics with file:line), `node_lint` (eslint → findings with rule and position), `node_outdated`, `node_audit`. The source itself: `js_files` (imports, exports, functions, classes, JSX, the relative imports resolved into a module graph with its import cycles) and `analyze_js` (JS0001–JS0017 over masked source; a relative specifier is resolved to a file, a package specifier is not). It has a module graph but no parser or type model — no symbols, rename or refactoring in it |
| **HTML / CSS** | `markup_files` (per file: elements, distinct classes, simple selectors, duplicate ids) and `analyze_markup` (HTML0001–HTML0015, CSS0001–CSS0009), over a quote-aware tag scanner. The page's tag/class/id index is matched against the **simple** selectors of the scanned stylesheets (tag, `.class`, `#id`, `tag.class`): a selector that matches nothing is CSS0009, a class used in markup and defined in no scanned stylesheet is HTML0015. There is no DOM: combinators, attribute selectors, pseudo-classes, specificity and the cascade are not resolved, selectors are matched set-against-set rather than element-against-rule, a stylesheet loaded from a CDN is outside the scan, and CSS-in-JS or CSS-isolation analysis does not exist |
| **SQL** | `sql_files` (statements, verbs, tables) and `analyze_sql` (SQL0001–SQL0010, comments and literals masked first). A pattern scanner, not a dialect parser: no schema model, no validation against a database, no execution or query plan |
| **EF Core** | The `DbContext` and entities are C# and fully analysed; migrations run through `ef_migrations` (`dotnet ef`), including `script` (the migration SQL) and `pending-model-changes` (does the model still match the snapshot). The SQL a single query will generate and a field-level diff between migrations are still absent |
| **Kubernetes / Helm** | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs` read the cluster; `k8s_apply`, `k8s_delete`, `k8s_rollout` write with `confirm=true` (apply defaults to a server-side dry run); `k8s_helm` covers releases; `yaml_files`/`analyze_yaml` read a manifest's identity and structure. No manifest/chart schema validation, no kustomize, no exec/port-forward |
| **Python / C++ / Java / Go / Rust** | `translate_python_file` / `translate_cpp_file` lower a file to pseudocode through its AST dump (no type checker, no navigation); Java, Go and Rust have labels in the model, not frontends |

Full inventory, with the exact boundary for each gap: [docs/gaps.md](../gaps.md).

Beyond languages, the same page records what is absent from the .NET workflow itself: the debugger is
engine-level (16 `debug_*` tools, `debug_test` included; edit-and-continue and hot reload are not
supported by the adapter), decompiled code is a **navigable Roslyn workspace** (`assembly_workspace`)
with an IL view (`disassemble`) but no rename and no debugging inside it, and the data-flow analysis
(`trace_data_flow`) follows locals, parameters, call arguments and a field of the same type; a value
parked in a field of another object, a database or a queue is not followed.

## Scope of the product

VisualMCP is not:

- an IDE replacement — it has no editor and no project designer; it does have a debugger engine
  (`debug_*`), but the breakpoint list, call-stack and watch windows are the client's to draw;
- a test framework — it runs your tests, it does not replace them; `verify_change` compares two runs, it
  does not invent the coverage a suite lacks;
- a proof of correctness or security — `find_security_issues` and the Razor rules find patterns, not
  proofs, and `verify_change` measures the behaviour the tests cover;
- a remote service — there is no hosted component, and running the stdio server on another machine would
  break its paths;
- a guarantee of API compatibility after an upgrade — `update_solution` proves the build, not the
  behaviour. Run the tests.

## Licensing

The source is available under the **PolyForm Noncommercial License 1.0.0**. Noncommercial use is free;
commercial or company use requires a separate paid license. See [LICENSE](../../LICENSE) and
[CONTRIBUTING.md](../../CONTRIBUTING.md).

---

Next: [Help and FAQ](../help.md)
