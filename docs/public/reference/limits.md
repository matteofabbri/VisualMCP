[Docs home](../README.md) › **Limits**

# Limits

A tool is only trustworthy if it is clear about what it cannot do. This page is the honest list.

## Platform

- **Windows, Linux and macOS are all supported.** There is an installer for each: `scripts/install.ps1`
  (PowerShell, Windows) and `scripts/install.sh` (POSIX shell, Linux/macOS) publish the server, register
  it for ZCode and Claude Code and install the routing guard; both take the same scopes and write the
  same config shapes. The server itself picks the platform's shell, profiling tools and updater at
  runtime — see [Getting started → Requirements](../getting-started.md#requirements).
- **The .NET 10 SDK is required.** The server targets `net10.0` and loads solutions through
  `MSBuildWorkspace`.
- **The routing guard needs Python 3 on Linux/macOS.** Its hook reads a JSON payload, so the POSIX hook
  is a small Python script; the installer registers it only when an interpreter is found and says so
  when one is not (the server install is unaffected). The Windows hook is PowerShell and needs nothing
  extra.
- **A solution must evaluate and restore.** If MSBuild cannot load a project — a missing SDK, a broken
  `Directory.Build.props`, a package feed that is unreachable — the semantic model degrades to MSBuild
  diagnostics. It does not silently fall back to text search.
- **Visual Studio is not required**, but some environment tools (`get_environment_info`) report only what
  is actually installed, and MSVC/`vswhere` exist only on Windows by nature.

## Analysis

Nothing in this section is an open limitation. Each boundary of a static analysis has a tool that
measures it; the two facts that used to be list entries are preconditions, stated once at the end.

| Question | Answer |
|---|---|
| **Did my change alter behaviour?** | `verify_change` runs the suite on the working tree and on a git revision checked out in a detached worktree, then diffs the outcomes test by test: regressions, fixes, new and removed tests. A run no test covers comes back `unverified`, which is the answer. |
| **Is this symbol reached only by reflection?** | `find_reflection_usage` lists every reflective lookup with the name it uses, and resolves the names that match a declaration in the solution — those are the ones to treat as live. |
| **Is this member called by a framework convention?** | `framework_entry_points` (and the `frameworkInvoked` list of `find_unused_symbols`): MVC actions, Razor Pages handlers, hub methods, hosted services, Blazor lifecycle and parameters, EF overrides and DbSets, middleware, test methods, `[JSInvokable]`, `[Inject]`. |
| **Is that type generated, and by what?** | `list_generated_sources` reports the generators a project references and the trees they emit — present in the compilation, absent from `project.Documents`, which is why they have no readable source. |
| **What do we use from a referenced assembly?** | `find_external_usages` reports the used surface of assemblies outside the solution, with call sites and a per-assembly summary; internals stay out of scope by definition. |
| **How much should I trust a finding?** | Every finding carries its rule id and severity, `analyze_complexity` states a confidence, and `inspection_baseline` turns the findings into a gate for a change. |

**Two preconditions, stated here rather than as gaps.** The compiler gate proves a change *compiles*, never
that it is correct: behaviour is measured by `verify_change`, and code the suite does not cover is reported
as `unverified` instead of being called safe. Heuristic rules (`analyze_complexity`, the Razor rules,
security scanning) report patterns with their confidence — they are finders, and a green run means "no
pattern matched", which `inspection_baseline` makes meaningful by comparing against the previous state.

## The style engine (`simplify_code`)

No id in Microsoft's code-style table is left unanswered, and none of the answers is a limitation of a
rewriter:

| Category | Ids | What it means |
|---|:---:|---|
| **Rewritten** | 97 of 126 | `simplify_code` applies the rewrite, under the compiler gate and (for the rules that claim equivalence) the operation-level check. |
| **Delegated** | 3 | `IDE0005`, `IDE0032`, `IDE0290`: `simplify_code` accepts the id and answers with the dedicated engine — `optimize_usings`, `property_to_auto`, `convert_to_primary_constructor`. |
| **Not applicable to C#** | 5 | `IDE0081`/`IDE0084`/`IDE0140` are Visual Basic's (`simplify_vb_code` owns them); `IDE1007` is a compiler diagnostic; `IDE0370` was split by Microsoft into `IDE0240`/`IDE0241`, both rewritten here. |
| **Not a rewrite** | 21 | Refactorings (`IDE0050` anonymous type→tuple, `IDE0210`/`IDE0211` top-level statements, `IDE0303`–`IDE0305` collection expressions for builders, `IDE0330` `System.Threading.Lock`, `IDE0410` labeled jumps), changes of contract (`IDE0064`), a rename policy (`IDE1006`, applied by `naming_rules` rather than as a syntax rewrite), layout owned elsewhere (`IDE0065`, `IDE0360`), a suppression audit (`IDE0076`/`IDE0077`), semantic decisions (`IDE0110`, `IDE1005`, `IDE0221`, `IDE0350`), and a Copilot suggestion (`IDE3000`). Each one is named, with its category, in `simplify_code`'s own answer for that id. |

**Team preferences are option-driven, not assumed.** `csharp_style_var_*` (`IDE0007`/`IDE0008`),
`dotnet_style_parentheses_in_*` (`IDE0048`) and the seven blank-line options (`IDE2000`–`IDE2006`)
apply only when the repository configures them; without the option the rule does nothing and the result
says which id waited for which option. `csharp_prefer_braces`, the modifier order, accessibility, the
namespace form, the foreach cast and the file header work the same way. Visual Basic is a separate
layer with its own catalogue (`simplify_vb_code`); the C# engine does not touch `.vb` files.

**What every write guarantees.** A rule rewrites one region and may edit several documents — the
suggestion is planned against the whole solution and applied all-or-nothing. Rules that claim
equivalence are compared operation-by-operation and refused on a difference (reported under
`semantics`). The gate is per document: one that would gain compiler errors is refused while the other
documents of the run still apply. For the rules that do not claim equivalence the gate is compilation,
so review the diff every dry run returns. `IDE0055` (formatting) stays opt-in and is not part of
`rules=all`.

## Refactoring

The refactorings are semantic, compiler-gated, and refuse what they cannot do safely. What used to be
listed here as limits are exactly those guarantees, so they are stated as guarantees; operations the
engine does not offer at all are tracked in
[What is missing → Refactoring gaps](../../../docs/gaps.md#7-refactoring-gaps), not here.

- **They refuse rather than approximate.** A method group or `nameof` use that a rename would change, an
  object initializer a factory cannot express, a member selection that is not self-contained, an
  interface implementation whose signature must stay identical: each is refused with the reason instead
  of producing a broken build.
- **A stale or contended write is refused, never lost.** Every write goes through one guarded path: it
  re-reads the file, refuses when the file on disk no longer matches the solution (an editor, or another
  tool call with an older plan), and serialises the check-and-write per file path. Two calls that edit
  the same file cannot lose an update — the second is refused and says why. `run_plan`'s `paths` still
  keeps overlapping steps apart; the gate is the safety net behind it.
- **Undo is the edit history and Git.** `edit_history` records every write this server made, `revert_edit`
  restores one, and it refuses when the file changed after that edit — the guarantee that a revert never
  silently discards newer work. There is no in-editor undo stack because there is no editor (section 11).
- **Dry run follows the tool's schema.** The preview-first family (`simplify_code`, `apply_code_fix`,
  `razor_edit`, most refactorings) defaults to it and returns the diff; the write-through family
  (`apply_rename`, `change_signature`, `safe_delete`, `generate_members`, `optimize_usings`,
  `reorder_members`, …) applies immediately and is recorded, so it is still revertible.
- **Some operations are deliberately syntax-level**, for example `convert_to_primary_constructor` when the
  constructor is a pure set of assignments. Anything more complex is refused, not guessed — a refactoring
  that changes behaviour is worse than one that does not run.

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

- The model covers the languages it has frontends for: **C#, Razor, Ghidra snapshots and canonical
  pseudocode**. Python and C++ are read from their AST dumps by `translate_python_file` and
  `translate_cpp_file`, which produce the dump when the interpreter or compiler is installed (or take
  one you supply): no type checking, no symbol navigation, no editing.
- **Unsupported constructs stay visible** as `RAW[lang] … // reason` plus a diagnostic. A translation can
  therefore be incomplete; the diagnostics say where.
- **Ghidra is an external dependency** with a real runtime cost. `analyze_native_binary` can take tens of
  seconds, is bounded by a timeout, and needs a JDK 21+. Ghidra is downloaded once when no installation
  is found (a few hundred MB); set `GHIDRA_INSTALL_DIR` to use a copy that is already there.
- **Decompiled output is text.** Rename, navigation and semantic analysis apply to source documents, not
  to decompiled assemblies.
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
| **Visual Basic** | The **symbol index** (`find_symbol`-style queries over `reference_index`: declarations, uses by kind, hierarchy, outline, metacode, unused); **metacode** (translate, compare, index trees and complexity, generate); `structural_search` with VB patterns (`language=auto|cs|vb`); `format_code`, `optimize_usings` (for `Imports`) and `code_cleanup`; **inspections** through the packs that ship VB analyzers (the .NET analyzers' `vb` set and the VMC0001 house rule — the C#-only packs say they do not cover the project); **analysis** (`find_dead_code`, `find_code_smells`, `find_async_anti_patterns`, `find_performance_anti_patterns`, `complexity_report`, `get_complexity_hotspots`); **refactorings** `apply_rename`, `encapsulate_field`, `add_params`/`remove_params`; `simplify_vb_code` (15 ids, 34 refused with a reason); `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `run_code_analysis`, `text_index`. The refactorings that are C# syntax or C# idiom refuse with that reason; `get_metrics`, `analyze_complexity`, `get_cohesion_metrics`, `find_duplicate_code`, `fix_complexity` and the generation tools remain C#-only. A solution with **no C# project** is a supported workspace: `check_solution` reports its Visual Basic projects and names the tools that apply. A runnable sample is [samples/VbDemo](../../../samples/VbDemo/README.md) |
| **Razor / Blazor** | A heuristic markup scanner (28 rules) and verified edits. It is not a Razor compiler: `@model` members, tag helpers, component parameters, cascading values, `@bind` types and CSS isolation are not type-checked |
| **F#** | `fsharp_files` lists the declarations (modules, types, top-level functions, opens) and `analyze_fsharp` applies FS0001–FS0010 over masked source; `build_project` returns the F# compiler's own `FS####` diagnostics and `run_tests` runs an F# test project. There is no semantic model (F# is not a Roslyn language), so no symbols, references, refactorings or inspections — the scanner resolves nothing |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | A heuristic markup layer: inventory, nine rules (`x:Name`/`x:Key` duplicates, `x:Class`, event handlers, resource keys, `DataTemplate`, name collisions, XML) and navigation (`x:Name` ↔ field, key ↔ uses, `BasedOn` chain). The XAML is not compiled, so bindings, tag helpers and styles are not type-checked; no designer, no XAML refactorings. The C# code-behind is fully analysed |
| **JavaScript / TypeScript** | The toolchain, through the project's own npm/pnpm/yarn: `node_info`, `node_install`, `node_script`, `node_typecheck` (tsc → `TSxxxx` diagnostics with file:line), `node_lint` (eslint → findings with rule and position), `node_outdated`, `node_audit`. The source itself: `js_files` (imports, exports, functions, classes, JSX) and `analyze_js` (JS0001–JS0016 over masked source). It has no parser or module graph — no symbols, rename or refactoring in it |
| **HTML / CSS** | `markup_files` and `analyze_markup` (HTML0001–HTML0014, CSS0001–CSS0008), over a quote-aware tag scanner. There is no DOM: selectors are not matched against a document, and CSS-in-JS or CSS-isolation analysis does not exist |
| **SQL** | `sql_files` (statements, verbs, tables) and `analyze_sql` (SQL0001–SQL0010, comments and literals masked first). A pattern scanner, not a dialect parser: no schema model, no validation against a database, no execution or query plan |
| **EF Core** | The `DbContext` and entities are C# and fully analysed; migrations run through `ef_migrations` (`dotnet ef`). There is still no model-snapshot diff and no generated-SQL inspection |
| **Kubernetes / Helm** | `k8s_status`, `k8s_context`, `k8s_get`, `k8s_describe`, `k8s_logs` read the cluster; `k8s_apply`, `k8s_delete`, `k8s_rollout` write with `confirm=true` (apply defaults to a server-side dry run); `k8s_helm` covers releases; `yaml_files`/`analyze_yaml` read a manifest's identity and structure. No manifest/chart schema validation, no kustomize, no exec/port-forward |
| **Python / C++ / Java / Go / Rust** | `translate_python_file` / `translate_cpp_file` lower a file to pseudocode through its AST dump (no type checker, no navigation); Java, Go and Rust have labels in the model, not frontends |

Full inventory, with the exact boundary for each gap: [docs/gaps.md](../../../docs/gaps.md).

Beyond languages, the same page records what is absent from the .NET workflow itself: the debugger is
engine-level (16 `debug_*` tools, `debug_test` included; edit-and-continue and hot reload are not
supported by the adapter), decompiled code is a **navigable Roslyn workspace** (`assembly_workspace`)
with an IL view (`disassemble`) but no rename and no debugging inside it, and the data-flow analysis
(`trace_data_flow`) follows locals, parameters and call arguments only — a value parked in a field, a
database or a queue is not followed.

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
commercial or company use requires a separate paid license. See [LICENSE](../../../LICENSE) and
[CONTRIBUTING.md](../../../CONTRIBUTING.md).

---

Next: [Help and FAQ](../help.md)
