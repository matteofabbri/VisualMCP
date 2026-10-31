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

- **87 of the 126 rule ids** in Microsoft's code-style table are rewritten. The other 39 are returned in
  `notRewritten` with a written reason — none is silently ignored, and three of them (`IDE0005`,
  `IDE0032`, `IDE0290`) are accepted by `simplify_code` and delegated to their own tools.
- Visual Basic: **15 rewritten ids**, 34 with a reason. The VB layer is separate; `simplify_code` does
  not touch `.vb` files.
- **A rule rewrites one region and may edit several documents.** A suggestion whose edits name other files
  is planned against the whole solution and applied all-or-nothing: if any file it touches is refused by
  the compiler gate, none of it is written.
- **Rules that claim equivalence are proved, not assumed.** They are compared operation-by-operation
  against the code they replace; a difference refuses the file. The result reports it under `semantics`.
- **The gate is per document, not per edit.** A document whose change would add errors is refused as a
  whole; other documents in the same run still apply.
- **For rules that do not claim equivalence, the gate is compilation, not equivalence.** Such a rewrite
  can compile and still change meaning. Review the diff, which every dry run returns.
- **Only a small set of `.editorconfig` keys is read** (five for C#, three for VB). Preferences outside
  that set — most importantly the `csharp_style_var_*` family and naming rules — are not applied.
- **`IDE0055` (formatting) is opt-in.** It is not part of `rules=all`, because formatting a whole
  project is a deliberate act.
- **Naming rules are not rewrites.** `IDE1006` and similar require a rename, which is a different
  operation.

## Refactoring

- Refactorings **refuse rather than approximate**. Common refusals: a method group or `nameof` use that a
  rename would change, an object initializer that a factory cannot express, a member selection that is
  not self-contained, an interface implementation whose signature must stay identical.
- Some operations are deliberately **syntax-level** (for example `convert_to_primary_constructor` when
  the constructor is a pure set of assignments). Anything more complex is refused, not guessed.
- **Undo is the edit history and Git**, not an in-editor undo stack. `revert_edit` refuses if the file
  changed after the edit.
- **Not every operation has a dry run.** The tool schema says whether `dryRun` exists and its default;
  the write-through family (`apply_rename`, `change_signature`, `safe_delete`, `generate_members`,
  `optimize_usings`, `reorder_members`, …) changes files immediately.
- **Parallel edits must be separated by path.** `run_plan` honours `paths`; two tools called concurrently
  by hand do not.

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

The semantic engine is Roslyn over C#. Everything else is a dedicated scanner (Razor/Blazor), a style
layer (Visual Basic) or text indexing. This is the whole map:

| Language / platform | Coverage |
|---|---|
| **C#** | The full surface: navigation, references, refactorings, style, inspections, complexity, profiling, decompilation |
| **Visual Basic** | `simplify_vb_code` (15 ids, 34 refused with a reason); the Roslyn navigation that does not need C# syntax (`find_symbol`, `find_references`, `find_implementations`, `get_type_members`, `preview_rename`); `build_project`, `run_tests`, `run_project`, `get_diagnostics`, `text_index`; `inspect_code` returns the findings of the language-agnostic CA analyzers (C#-only analyzers are reported as ignored crashes). `read_symbol` returns the declaration header only. No VB formatter, built-in inspection packs, complexity analysis, symbol index, test-quality scan or metacode frontend. A solution with **no C# project** is a supported workspace: `check_solution` reports its Visual Basic projects and names the tools that apply. A runnable sample is [samples/VbDemo](../../../samples/VbDemo/README.md) |
| **Razor / Blazor** | A heuristic markup scanner (28 rules) and verified edits. It is not a Razor compiler: `@model` members, tag helpers, component parameters, cascading values, `@bind` types and CSS isolation are not type-checked |
| **F#** | `.fs`/`.fsproj` are full-text indexed and can be built/tested with `dotnet`; there is no semantic model, so no symbols, references, refactorings or inspections |
| **XAML — WPF, WinUI 3, MAUI, Avalonia, UWP** | A heuristic markup layer: inventory, nine rules (`x:Name`/`x:Key` duplicates, `x:Class`, event handlers, resource keys, `DataTemplate`, name collisions, XML) and navigation (`x:Name` ↔ field, key ↔ uses, `BasedOn` chain). The XAML is not compiled, so bindings, tag helpers and styles are not type-checked; no designer, no XAML refactorings. The C# code-behind is fully analysed |
| **JavaScript / TypeScript / HTML / CSS** | Text only. No parser, type checker, module graph, linting or npm integration |
| **SQL** | Text only. No dialect parser, schema model, query validation, execution or query plans |
| **EF Core** | The `DbContext` and entities are C# and fully analysed; migrations run through `ef_migrations` (`dotnet ef`). There is still no model-snapshot diff and no generated-SQL inspection |
| **Kubernetes / Helm** | Nothing. Docker covers local containers and compose only; manifests are text |
| **Python / C++ / Java / Go / Rust** | `translate_python_file` / `translate_cpp_file` lower a file to pseudocode through its AST dump (no type checker, no navigation); Java, Go and Rust have labels in the model, not frontends |

Full inventory, with the exact workaround for each gap: [docs/gaps.md](../../../docs/gaps.md).

Beyond languages, the same page records what is absent from the .NET workflow itself: the debugger is
engine-level only (16 `debug_*` tools; edit-and-continue, hot reload and debugging a unit test are
missing), **no live/postfix/file templates**, inspection severity and suppression are written per rule
but there is **no background analysis daemon** (`inspection_baseline` is the CI substitute), **no test
debugging and no continuous testing** (single-test runs, re-running the failures, test scaffolding and
`run_mutation_tests` exist), and decompiled code is now a **navigable Roslyn workspace** (`assembly_workspace`)
with no IL view, no rename and no debugging inside it.

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
