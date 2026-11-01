# VisualMCP documentation

**Compiler-grade code intelligence for your AI client — running on your own machine.**

VisualMCP is a local [Model Context Protocol](https://modelcontextprotocol.io) server for .NET. It gives
an AI coding client — Claude Code, ZCode, or any MCP-capable tool — **420 tools** that read a solution the
way an IDE does: through Roslyn's semantic model and the real project graph, not through regular
expressions over text files.

The server is a normal executable on your PC. Your client starts it as a child process and talks to it
over stdin/stdout. There is no hosted service, no account, and no remote endpoint.

---

## Why it exists

An agent working on a .NET solution without a compiler hits the same four walls:

| The question | Text search gives you | VisualMCP gives you |
|---|---|---|
| Where is this method used? | every line that happens to contain the name, including comments, strings and unrelated types | every real reference, resolved through overloads, overrides, generics and interface dispatch |
| Show me this class | a guess about which file it is in, then the whole file | the declaration's source with line numbers — one call |
| Why doesn't it compile? | the tail of a build log | structured errors and warnings with file, line and severity |
| Rename this safely | a find/replace that misses call sites and renames unrelated text | a semantic rename that updates every reference in the solution |

The same idea extends past navigation: analysis tools build control-flow graphs, refactorings rewrite
call sites, the style engine applies IDE quick fixes under a compiler gate, profiling tools read real
EventPipe traces, and Razor pages are analysed as markup with CWE-tagged security rules.

## What is in the box

- **Navigation and understanding** — symbols, references, callers, implementations, inheritance, type
  outlines, declaration source, project docs.
- **Analysis** — whole-solution audit, compiler diagnostics **and the mechanical ones fixed**
  (`fix_diagnostics`), inspections with code fixes, cyclomatic and cognitive complexity, Big-O estimates,
  churn × complexity hotspots, dead code, duplicates, security, dependency injection, namespace coupling,
  async anti-patterns.
- **Refactoring** — 60+ semantic refactorings, from rename and extract method to extract class, change
  signature and move type, most of them compiler-gated.
- **Code style** — `simplify_code` applies 87 of the 126 Microsoft code-style rule ids, with a
  per-document compiler gate and a recorded, revertible edit history; `simplify_vb_code` covers 41 Visual
  Basic ids.
- **Razor, Blazor and XAML** — page/component inventory, 28 Razor markup/accessibility/security rules,
  verified edits, and a XAML layer (`XAML0001`–`XAML0015`: names, keys, handlers, resources, templates and
  **binding paths checked against the data type**, with the project's dialect detected) plus the binding
  inspector, the element tree, name/resource/style navigation and verified edits — the code-behind is
  resolved through Roslyn.
- **Runtime work** — build, debug through a real DAP debugger, run, test with coverage, benchmark, profile
  a live .NET process (CPU, GC, allocations, I/O, locks, dumps) and get a plain-language verdict.
- **The wider loop** — NuGet package facts and vulnerability fixes, .NET modernization, EF Core
  migrations, Git, GitHub, Docker, repository scaffolding, a solution-scoped kanban board, and `run_plan`
  for executing a whole dependency graph of tool calls in one round trip.
- **Metacode** — one canonical pseudocode model for C#, Razor, decompiled .NET assemblies (ILSpy) and
  native binaries recovered with Ghidra.

## Quick start

Requirements: **Windows 10/11** and the **.NET 10 SDK**. Visual Studio is not required.

```powershell
git clone https://github.com/matteofabbri/VisualMCP.git C:\REPOSITORY\VisualMCP
powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope all
```

The script builds a Release binary, publishes it under your user profile, and registers the server in
ZCode and Claude Code. **Restart the client**, open a folder containing a `.sln` or `.slnx`, and ask:

> Audit this solution and tell me where to start.

The solution is discovered and loaded on demand — you do not need to call `load_solution` first. Full
walkthrough: [Getting started](getting-started.md).

## Documentation map

| Page | What it answers |
|---|---|
| [Getting started](getting-started.md) | Requirements, install scopes, verification, first session, upgrade and uninstall |
| [How it works](how-it-works.md) | The process model, Roslyn loading, the solution store, the dashboard, the safety model, privacy |
| [Tool catalogue](tools.md) | All 420 tools, grouped by area, with a one-line description of each |
| [Tool arguments](reference/tool-arguments.md) | The arguments and behaviour that are not obvious from a one-line catalogue entry |
| [Reading and analysing code](guides/code.md) | Navigation, diagnostics, audits, complexity, security, dead code, dependency analysis |
| [Refactoring and style](guides/refactoring.md) | The refactoring catalogue, `simplify_code`, inspections and code fixes, and what "safe" means here |
| [Razor, metacode and decompilation](guides/razor-metacode.md) | Page analysis and edits, canonical pseudocode, ILSpy and Ghidra |
| [Build, test, packages and modernization](guides/runtime.md) | Build/run/test, coverage, benchmarks, NuGet, Aspire, framework and package upgrades |
| [Profiling](guides/profiling.md) | Native CPU/GC/heap/I/O profiling, the verdict tools, artifacts and limits |
| [Workflow and integrations](guides/workflow.md) | `run_plan`, planning, memory, Git, GitHub, Docker, files, HTTP and SignalR |
| [Configuration reference](reference/configuration.md) | Environment variables, paths, ports, store tables, dashboard API |
| [Limits](reference/limits.md) | The honest list of what the product does not do, and why |
| [Help and FAQ](help.md) | Troubleshooting, common questions, how to get support |
| [Contributing](contributing.md) | Build, test, architecture rules, CLA |

### Engineering notes

The pages that describe how the engine is built, what it covers and what it does not. They are allowed
to name internal types; the pages above are not.

| Page | What it answers |
|---|---|
| [engine-state.md](engine-state.md) | which IDE rules are rewritten, which are not and why, the structural limits of the rewrite engine, the state of the solution store |
| [gaps.md](gaps.md) | **everything the product does not do**: language and platform gaps, test debugging, mutation testing, template and inspection-administration gaps |
| [metacode.md](metacode.md) | the canonical pseudocode model: grammar, frontends, decompilation, indexing, generation, limits |
| [profiling.md](profiling.md) | every `profile_*` tool, what it reads, the artifacts, the known limits and the comparison with Visual Studio |
| [razor.md](razor.md) | the Razor/Blazor analysis layer and its 28 rules |
| [dashboard.md](dashboard.md) | the web dashboard: pages, query parameters, JSON API, safety rules |
| [modernization.md](modernization.md) | `aspire`, `update_framework`, `update_solution` |
| [adding-a-language.md](adding-a-language.md) | how a new language joins the style engine or the metacode model |
| [agent-ergonomics.md](agent-ergonomics.md) | how many calls an agent needs to answer one question, measured on the session store, with the chains that are still mechanical |
| [resharper-codemaid-coverage.md](resharper-codemaid-coverage.md) | the rule-by-rule comparison against ReSharper / Rider / CodeMaid |
| [resharper_tocover.md](resharper_tocover.md) | the whole ReSharper inspection catalogue — all 1,560 ids Rider 2026.2.1 declares, every language group, each with what the rule reports and whether a capability here answers it |
| [resharper-context-actions.md](resharper-context-actions.md) | the context-action catalogue and its behaviour |

The rule-by-rule table for the style engine is [`../CAPABILITY_MAP.md`](../CAPABILITY_MAP.md).

### House rules

- **Counts are live facts.** A tool count, "87 rule ids", "28 Razor rules" — all of them age badly:
  take the number from `capability_report` when you write it, and say where it came from.
- **`gaps.md` is the honest inventory.** A feature is not documented as present until it is in the
  tool catalogue and the engine applies it; anything else belongs in `gaps.md`.
- **User-facing pages do not name internal types.** If a sentence needs `SnitchContext` or
  `CompilationCache` to make sense, it belongs in the engineering notes.
- **Links are relative and checked.** The documentation is read on GitHub, so a renamed page must be
  renamed in every index at once.

## Privacy, stated precisely

VisualMCP itself **does not upload anything**. It has no remote service; it opens files on your disk and
answers over stdio. The only network traffic is what a specific tool's job requires — a NuGet restore
during a build, a package or template upgrade, an explicit `download_file`, the GitHub CLI, a symbol
download, the update check, or the one-time Ghidra download used by `analyze_native_binary`.

That is not the same as "your code never leaves the machine". Your AI client sends the conversation —
including the tool results it reads, such as source returned by `read_file` or `read_symbol` — to its
model provider. VisualMCP cannot change that; it only makes sure the server side of the conversation
stays local. Read [How it works](how-it-works.md#privacy) for the full picture.

## Status

- **Platform:** Windows, Linux and macOS are all supported, each with its own installer
  (`scripts/install.ps1`, `scripts/install.sh`); the server adapts its shell, debugger, profiler and
  updater to the platform it runs on.
- **Languages:** C# is the main surface; Visual Basic is a supported language (symbol index, metacode,
  structural search, formatting/usings/cleanup, the VB-capable inspection packs and their fixers, the
  analysis tools, `context_action` for the 33 ids that have a VB spelling, and the refactorings that are
  not C#-idiom-specific) plus the language-neutral build/test/diagnostic
  tools; Razor and Blazor markup are analysed by a dedicated
  scanner, and XAML has its own markup layer (inventory with the project's dialect, `XAML0001`–`XAML0015`
  diagnostics with the binding paths checked against their data type, the binding inspector, the element
  tree, navigation and verified edits) with the code-behind resolved through Roslyn. EF Core migrations
  run through `ef_migrations`; SQL
  (`sql_files`, `analyze_sql`, SQL0001–SQL0012), JavaScript/TypeScript (`js_files`, `analyze_js`,
  JS0001–JS0017, with every specifier resolved through a relative path, `tsconfig.json` `baseUrl`/`paths`
  or `node_modules`, and **`js_symbols`** — a lexical symbol table: outline,
  find, refs, unused, rename), HTML/CSS (`markup_files`, `analyze_markup`), YAML
  (`yaml_files`, `analyze_yaml`, YAML0001–YAML0012) and F#
  (`fsharp_files`, `analyze_fsharp`, and **`fsharp_symbols`** — the same questions plus a lexical rename)
  each have a scanned layer of their own; the JavaScript/TypeScript
  toolchain also runs through the project's own npm/pnpm/yarn (`node_*`, with `tsc`/`eslint` interpreted),
  and Kubernetes/Helm through `k8s_*`, with `k8s_validate` checking a manifest offline (and, with
  `server=true`, through the cluster's own `kubectl apply --dry-run=server`) and
  `k8s_port_forward` keeping a forwarded port as a session.
  A runnable Visual Basic sample lives in [`samples/VbDemo`](../samples/VbDemo/README.md), with the
  measured tool-by-tool results in [`DEMO.md`](../samples/VbDemo/DEMO.md).
  **What stays outside**: a scanner proves the shape of a line, and its symbol table resolves a name the
  token stream can follow, never a type or a schema — the JS/TS source has a module graph (its specifiers
  followed through relative paths, `tsconfig.json` `baseUrl`/`paths` and `node_modules`) and a lexical
  symbol table but no type model, HTML/CSS have no DOM, YAML has no general schema (a Kubernetes manifest
  is checked by `k8s_validate` against the built-in kinds unless `server=true` asks the cluster for its
  OpenAPI and admission answer), and F# has no semantic
  layer (its compiler's diagnostics come through `build_project`). See
  [Limits → Language and platform coverage](reference/limits.md#language-and-platform-coverage).
- **Scope:** everything is static analysis or process instrumentation. No tool proves that a change
  preserves behaviour in general — the style rules that claim equivalence are checked operation by
  operation — and no tool replaces your test suite. See [Limits](reference/limits.md).

## License

VisualMCP is **source-available** under the
[PolyForm Noncommercial License 1.0.0](../LICENSE): free for noncommercial use. Commercial or
company use requires a separate paid license. Contributions are accepted under the terms in
[CONTRIBUTING.md](../CONTRIBUTING.md).
