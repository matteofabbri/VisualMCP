# VisualMCP documentation

**Compiler-grade code intelligence for your AI client — running on your own machine.**

VisualMCP is a local [Model Context Protocol](https://modelcontextprotocol.io) server for .NET. It gives
an AI coding client — Claude Code, ZCode, or any MCP-capable tool — **322 tools** that read a solution the
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
  per-document compiler gate and a recorded, revertible edit history; `simplify_vb_code` covers 15 Visual
  Basic ids.
- **Razor, Blazor and XAML** — page/component inventory, 28 Razor markup/accessibility/security rules,
  verified edits, and a XAML layer (`XAML0001`–`XAML0009`, name/resource/style navigation) with the
  code-behind resolved through Roslyn.
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
| [Tool catalogue](tools.md) | All 322 tools, grouped by area, with a one-line description of each |
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

- **Platform:** Windows is the primary, fully supported platform. CI also builds self-contained
  binaries for Linux x64 and macOS arm64.
- **Languages:** C# is the main surface (Roslyn); Visual Basic has a style layer (15 ids) plus the
  language-neutral build/test/diagnostic tools; Razor and Blazor markup are analysed by a dedicated
  scanner, and XAML has its own markup layer (inventory, `XAML0001`–`XAML0009` diagnostics, navigation)
  with the code-behind resolved through Roslyn. EF Core migrations run through `ef_migrations`.
  A runnable Visual Basic sample lives in [`samples/VbDemo`](../../samples/VbDemo/README.md), with the
  measured tool-by-tool results in [`DEMO.md`](../../samples/VbDemo/DEMO.md).
  **Everything else is text at most**: JS/TS/HTML/CSS, SQL as a language, Kubernetes and F# have no
  semantic support. See [Limits → Language and platform coverage](reference/limits.md#language-and-platform-coverage).
- **Scope:** everything is static analysis or process instrumentation. No tool proves that a change
  preserves behaviour in general — the style rules that claim equivalence are checked operation by
  operation — and no tool replaces your test suite. See [Limits](reference/limits.md).

## License

VisualMCP is **source-available** under the
[PolyForm Noncommercial License 1.0.0](../../LICENSE): free for noncommercial use. Commercial or
company use requires a separate paid license. Contributions are accepted under the terms in
[CONTRIBUTING.md](../../CONTRIBUTING.md).
