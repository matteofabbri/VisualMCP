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

- The scanner is **heuristic**. It does not follow values across view models or JavaScript.
- **Markup inside `@{ … }` and `@code { … }` is not analysed** — that is C#.
- The anti-forgery check is the one rule that **resolves the partials a form renders** (`<partial name=…>`,
  `@Html.Partial("…")`, the `RenderPartial` family) and looks for the token inside them, following the
  partials those include in turn. It searches their text, so a token built by a helper it does not name is
  a false positive to review, and a partial it cannot resolve counts as having no token. Duplicate ids are
  still checked **per file**.
- **Dynamic values are not guessed.** A rule whose answer depends on a runtime value stays silent.
- Files over **2 MB** and unreadable folders are reported as skipped, not analysed.
- `.cshtml`/`.razor` are not Roslyn documents. Tools that require the semantic model do not see them;
  `razor_*` reads them from disk instead.

## Metacode and decompilation

- **The pseudocode model is a syntax model, not a language service.** Python and C++ are read from their
  AST dumps by `translate_python_file` and `translate_cpp_file`, which produce the dump when the
  interpreter or compiler is installed (or take one you supply): no type checking, no symbol navigation,
  no editing. Java has a frontend (`JavaFrontend`) but still no symbol navigation; Go and Rust have labels
  in the model, not frontends.
- **Unsupported constructs stay visible** as `RAW[lang] … // reason` plus a diagnostic. A translation can
  therefore be incomplete; the diagnostics say where.
- **Ghidra is an external dependency** with a real runtime cost. `analyze_native_binary` can take tens of
  seconds, is bounded by a timeout, and needs a JDK 21+. Ghidra is downloaded once when no installation
  is found (a few hundred MB); set `GHIDRA_INSTALL_DIR` to use a copy that is already there.
- **JADX is an external dependency too**, for Java bytecode (`.class`, `.dex`, `.jar`, `.apk`, `.aar`, …).
  It is downloaded from its release feed (~70 MB) on first use, needs a JDK, and the run is bounded by a
  timeout. Java constructs the model has no node for — an anonymous class body, a labeled
  `break`/`continue`, a local class, `assert`, a switch expression, a method reference — stay
  `RAW[java]` with a diagnostic; a record's components lower to fields plus the canonical constructor.
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
- **`profile_file_io` is Windows-only and needs elevation.** It uses the kernel ETW file provider, which
  is a privileged session (administrator or the Performance Log Users group). It reports the process that
  performed each read/write, not the managed call stack; Linux would need eBPF/perf.
- **The native monitor is loopback-only and unauthenticated.** It answers bounded requests over HTTP on
  `127.0.0.1`, not the continuous `/live` or `/logs` streams of dotnet-monitor. Do not expose its port.

## Debugger

- **Edit-and-continue and hot reload are not available.** The adapter (netcoredbg) applies metadata
  deltas only over GDB/MI (`-apply-deltas`, `--hot-reload`), and its DAP mode — the one this server
  speaks — has no request for them; the .NET hot-reload agent is a separate protocol. The substitutes
  are `debug_restart rebuild=true` (edit → build → relaunch with the same breakpoints) and
  `debug_evaluate newValue` (change a value while stopped, then continue).
- **A data breakpoint is the solution's write sites, not a memory watchpoint.** The adapter exposes no
  `dataBreakpointInfo`/`setDataBreakpoints`, so `debug_data_breakpoints` resolves the member through
  Roslyn and breaks where the **source** writes it — assignment, compound assignment, `++`/`--`, a
  `ref`/`out` argument or an initializer. A write from code the solution does not contain (a framework
  setter, reflection, a serialiser) does not stop, and a local variable is refused because it lives in
  one frame.
- **Hit conditions are counted by this server, not by the adapter** (`>5`, `>=5`, `5`, `%3`). A stop
  that does not satisfy one is resumed transparently, so `debug_continue` can absorb several hits before
  it returns; `hitConditionSkips` says how many. A condition nothing can satisfy gives up after 500
  absorbed hits and returns the stop. The stop is matched to its breakpoint by file and line, so a
  breakpoint the runtime moved to a different line is not counted — the adapter reports no breakpoint
  id on the stop event.
- **Exception type filters match the full type name exactly** (`System.NullReferenceException`); base
  classes are not matched, so each type you care about is named, and `!Type` excludes one.
- **The debugger windows are the client's** — breakpoints list, modules, parallel stacks:
  `debug_sessions`, `debug_stack` and `debug_threads` return the data, nothing renders it.

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
| **Visual Basic** | The **symbol index** (`find_symbol`-style queries over `reference_index`: declarations, uses by kind, hierarchy, outline, metacode, unused); **metacode** (translate, compare, index trees and complexity, generate); `structural_search` with VB patterns (`language=auto|cs|vb`); `format_code`, `optimize_usings` (for `Imports`) and `code_cleanup`; **inspections** through the packs that ship VB analyzers (the .NET analyzers' `vb` set and the VMC0001 house rule — the C#-only packs say they do not cover the project) and **their code fixes** through `apply_code_fix` (the 100+ VB `CodeFixProvider`s in the .NET analyzers' `vb` assembly); **analysis** (`find_dead_code`, `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns`, `complexity_report`, `get_complexity_hotspots`, `get_metrics`, `get_cohesion_metrics`, `find_duplicate_code`, `analyze_complexity`); **context actions** (`context_action` routes a `.vb` document to 33 of its 41 ids — the guards, Try/If/Using wrapping, `Imports`, interpolation, digit separators, named arguments, var/explicit type, split declaration, StringBuilder, merge/split checks, conditional access, `Select Case`, LINQ both ways, For Each↔For, invert loop; the C#-syntax ids are refused with the reason); **refactorings** `apply_rename`, `encapsulate_field`, `add_params`/`remove_params`, `change_signature` (the declaration and every plain call site, parentheses optional), `change_type`, `use_base_type`, `type_to_partial`, `safe_delete`, `extract_interface` (an `Interface` plus the `Implements` clause on the type and on each member), `extract_method`, `move_type`, `move_to_folder` (the folder derived from the `Namespace` block), `property_to_auto`, `function_to_property` (both call spellings); `simplify_vb_code` (41 ids, 8 refused with a reason — six of them constructs VB does not have); `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `run_code_analysis`, `text_index`. What still has no VB path is the refactoring catalogue's declaration-shape tail — `extract_class`/`extract_superclass`, the inline family, the move family beyond `move_type`/`move_to_folder`, and the indexer conversions. A solution with **no C# project** is a supported workspace: `check_solution` reports its Visual Basic projects and names the tools that apply. A runnable sample is [samples/VbDemo](../../samples/VbDemo/README.md) |
| **Razor / Blazor** | A heuristic markup scanner (28 rules) and verified edits. The anti-forgery rule resolves the partials a form renders (`<partial name=…>`, `@Html.Partial("…")`, the `RenderPartial` family) and re-checks a nested include with a cycle guard. It is not a Razor compiler: `@model` members, tag helpers, component parameters, cascading values, `@bind` types and CSS isolation are not type-checked |
| **F#** | `fsharp_files` lists the declarations (modules, types, top-level functions, opens) and `analyze_fsharp` applies FS0001–FS0010 over masked source; `build_project` returns the F# compiler's own `FS####` diagnostics and `run_tests` runs an F# test project. There is no semantic model (F# is not a Roslyn language), so no symbols, references, refactorings or inspections — the scanner resolves nothing |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | A markup layer with the C# resolved through Roslyn: the **dialect of each project** (WPF / WinUI 3 / .NET MAUI / Avalonia / UWP, from UseWPF/UseMaui/UseWinUI/WindowsAppSDK/Avalonia/UWP and the Directory.Build.props chain) and MAUI multi-targeting; inventory (`xaml_pages`); fifteen rules (`analyze_xaml` XAML0001–XAML0015 — names, keys, `x:Class`, handlers, resources, templates, plus **binding paths validated against the data type** (`x:DataType`, a template's `DataType`, a `DataContext` the code-behind assigns, `x:Bind` against the page type, `TemplateBinding` against the `ControlTemplate`'s `TargetType`), `Setter`/`TargetType`, `x:Bind` per dialect, `ElementName`/`Storyboard.TargetName`, unresolvable `TargetType`); the binding inspector (`xaml_bindings`, every binding with its type, source and per-segment status); the element tree (`xaml_tree`); navigation; and verified edits (`xaml_rename`, `xaml_extract_style`, `xaml_promote_resource`, `format_xaml`). No rendered preview or designer, and no compiled XAML: a binding whose data type the markup does not name is `not-checked` rather than guessed, and a key coming from a referenced assembly's theme dictionary is invisible. The C# code-behind is fully analysed |
| **JavaScript / TypeScript** | The toolchain, through the project's own npm/pnpm/yarn: `node_info`, `node_install`, `node_script`, `node_typecheck` (tsc → `TSxxxx` diagnostics with file:line), `node_lint` (eslint → findings with rule and position), `node_outdated`, `node_audit`. The source itself: `js_files` (imports, exports, functions, classes, JSX, the relative imports resolved into a module graph with its import cycles) and `analyze_js` (JS0001–JS0017 over masked source; a relative specifier is resolved to a file, a package specifier is not). It has a module graph but no parser or type model — no symbols, rename or refactoring in it |
| **HTML / CSS** | `markup_files` (per file: elements, distinct classes, selectors, duplicate ids) and `analyze_markup` (HTML0001–HTML0015, CSS0001–CSS0009), over a quote-aware tag scanner. A selector resolves against the page's element index — compound parts with tag, classes, id, attribute conditions and the structural pseudo-classes `:root`/`:first-child`/`:last-child`/`:only-child`/`:empty`, joined by descendant, child, adjacent-sibling and general-sibling combinators — the CSS rules run on every `<style>` block with the finding reported on the page's line, and an inline `style` attribute is checked for `!important`, a `javascript:` URL and `expression()`; a selector that matches nothing is CSS0009, a class used in markup and defined in no scanned stylesheet is HTML0015. It is not a renderer: a selector whose meaning only a browser can decide (`:hover`, `:checked`, `:nth-child()`, `:has()`) is never reported as unmatched, the tag nesting is the one the markup writes (optional end tags are closed by the next sibling), specificity and the cascade are not resolved, and a stylesheet loaded from a CDN or built in JavaScript (CSS-in-JS) is outside the scan |
| **SQL** | `sql_files` (statements, verbs, tables) and `analyze_sql` (SQL0001–SQL0010, comments and literals masked first). A pattern scanner, not a dialect parser: no schema model, no validation against a database, no execution or query plan |
| **EF Core** | The `DbContext` and entities are C# and fully analysed; migrations run through `ef_migrations` (`dotnet ef`), including `script` (the migration SQL), `diff` (the SQL between two migrations) and `pending-model-changes` (does the model still match the snapshot). The SQL a single query will generate and a field-level comparison of the model itself against the snapshot are still absent |
| **Kubernetes / Helm** | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs` read the cluster and `k8s_kustomize` renders an overlay offline; `k8s_exec` runs one confirmed command inside a pod, and `k8s_apply`, `k8s_delete`, `k8s_rollout` write with `confirm=true` (apply defaults to a server-side dry run); `k8s_helm` covers releases; `yaml_files`/`analyze_yaml` read a manifest's identity and structure. No manifest/chart schema validation, no port-forward |
| **Python / C++ / Java / Go / Rust** | `translate_python_file` / `translate_cpp_file` lower a file to pseudocode through its AST dump (no type checker, no navigation); Java, Go and Rust have labels in the model, not frontends |

Full inventory, with the exact boundary for each gap: [docs/gaps.md](../gaps.md).

Beyond languages, the same page records what is absent from the .NET workflow itself: the debugger is
engine-level (20 `debug_*` tools, `debug_test` included; the [Debugger](#debugger) section lists what the
adapter cannot do), decompiled code is a **navigable Roslyn workspace** (`assembly_workspace`)
with an IL view (`disassemble`) but no rename and no debugging inside it, and the data-flow analysis
(`trace_data_flow`) follows locals, parameters, call arguments and a field written in one method and read
in another, on any object. A field is keyed by its declaration and not by the instance it hangs off, so
writing one instance's field taints every instance's; a property, a database and a queue are not followed.

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
