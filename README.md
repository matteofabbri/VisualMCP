# VisualMCP

An MCP server that gives an AI coding client — Claude Code, ZCode, or any MCP-capable tool — a .NET
solution the way an IDE sees it: the compiler's semantic model, the real project graph, real EventPipe
traces, and a compiler gate on every edit it makes. **314 tools in one local process.**
`capability_report` prints the live count, by area, from the running build.

> **User documentation:** [docs/public/README.md](docs/public/README.md) — install, concepts, the
> complete tool catalogue, task guides and limits.
> **This page is the map.** Every folder has its own README, linked below.

## What it looks like

Not a tool list. One call, on this repository:

```text
> Audit this solution and tell me where to start.

solution_audit  (excerpt)

Solution: VisualMCP.sln — 5 project(s), 715 file(s), 123,786 line(s)
Compiler: 0 error(s), 11 warning(s)
     3x CS8602  warning at StyleRewriteService.cs:492
     2x CS8600  warning at PackageInspectionService.cs:73
     …

Most complex methods (cyclomatic complexity, method length):
   62 complexity  268 lines  MoveInstanceMethod(7 args)     (MoveInstanceMethodService.cs:34)
   61 complexity  210 lines  PropertyToFunction(7 args)     (PropertyToFunctionService.cs:34)
   56 complexity  226 lines  MoveStaticMembers(6 args)      (MoveStaticMembersService.cs:33)
   55 complexity  235 lines  ExtractSuperclass(6 args)      (ExtractSuperclassService.cs:34)
   …

Project graph:
  Snitch            → (leaf)
  Snitch.Metacode   → Snitch
  Snitch.Profiling  → Snitch
  VisualMCP         → Snitch, Snitch.Metacode, Snitch.Profiling
  VisualMCP.Tests   → Snitch, Snitch.Metacode, Snitch.Profiling, VisualMCP

Suggested first moves:
  1. Extract from 'MoveInstanceMethod(7 args)' (complexity 62) — extract_method on its worst block.
```

The next call, on the same solution, ranks **1,426 methods** above the complexity floor and names the
decision points that own each score (`complexity_report`), or joins git history with that ranking to say
which file a refactoring pays on first (`git_churn_hotspots` — commits touching a file × the cyclomatic
complexity of its worst method).

## It fixes the build, not only reports it

`get_diagnostics` is the Error List. `fix_diagnostics` is the part an IDE does for you: it rewrites the
mechanical compiler diagnostics and refuses the rest by name.

| Id | What is fixed |
|---|---|
| CS0168, CS0219 | a local nothing reads — removed, or kept as a discard when the initializer has a side effect |
| CS0169 | a private field nothing uses |
| CS0067 | an event nothing raises |
| CS0105 | a `using` that appears twice |
| CS0108, CS0114 | a member that hides an inherited one — marked `new` |
| CS0109 | a `new` that hides nothing — removed |

Every fix is spliced on the syntax tree, then accepted only when the project compiles **no worse**; the
whole run is refused if the fixes together would add an error. Written edits go into the solution's edit
history and `revert_edit` undoes them one by one. `dryRun` is the default.

## Why it is not a wrapper around grep

| The question | Text search gives you | VisualMCP gives you |
|---|---|---|
| Where is this method used? | every line that contains the name, including comments, strings and unrelated types | every real reference, resolved through overloads, overrides, generics and interface dispatch |
| Show me this class | a guess about the file, then the whole file | the declaration's source with line numbers, in one call |
| Why doesn't it compile? | the tail of a build log | structured errors and warnings with file, line and severity — and the mechanical ones fixed |
| Rename this safely | a find/replace that misses call sites and renames unrelated text | a semantic rename that updates every reference in the solution |

The same idea runs through the rest of the product: analysis tools build control-flow graphs, 60+
refactorings rewrite call sites, the style engine applies IDE quick fixes under a compiler gate,
profiling tools read real EventPipe traces, and Razor pages are analysed as markup with CWE-tagged
security rules.

Four properties hold across the whole surface, and they are what make it safe to let an agent act:

- **Semantic, not textual.** Overloads, overrides, generics, partial types and interface dispatch are
  resolved by Roslyn; a same-named unrelated identifier is never touched.
- **Compiler-gated writes.** Anything that renames, moves, extracts, generates or cleans up is previewed
  by default and refused when the change would add a compiler error.
- **Recorded, revertible edits.** Every file this server writes is in the solution store with its before
  and after hash, and `revert_edit` restores it — including the history of the revert itself.
- **Read-only where it says so.** Analysis and profiling tools never modify the solution; the mutating
  ones are marked ✎ in the catalogue, and most default to a dry run.

## What is in the box

| Area | What it covers |
|---|---|
| Navigation and understanding | symbols, references, callers, call hierarchy, implementations, inheritance, type outlines, declaration source, project docs |
| Analysis | whole-solution audit, compiler diagnostics **and their mechanical fixes**, inspections with code fixes, baselines for CI, cyclomatic/cognitive complexity, Big-O, dead code, duplicates, security, dependency injection, namespace coupling, async and allocation anti-patterns |
| Refactoring | 60+ semantic refactorings, from rename and extract method to extract class, change signature and move type, most compiler-gated |
| Code style | `simplify_code` applies 87 of the 126 Microsoft code-style ids with a per-document compiler gate; `simplify_vb_code` covers 15 Visual Basic ids |
| Razor, Blazor and XAML | page/component inventory, 28 Razor markup/accessibility/security rules, verified edits, and a XAML layer with the code-behind resolved through Roslyn |
| Runtime work | build, debug through a real DAP debugger, run, test with coverage, benchmark, profile a live process (CPU, GC, allocations, I/O, locks, dumps) and get a plain-language verdict |
| The wider loop | NuGet facts and vulnerability fixes, .NET modernization, EF Core migrations, Git, GitHub, Docker, scaffolding, a solution-scoped kanban board, and `run_plan` for a whole dependency graph in one round trip |
| Metacode | one canonical pseudocode model for C#, Razor, decompiled assemblies (ILSpy) and native binaries (Ghidra), persisted per declaration |

A note on the headline number, because it is the easiest thing to inflate: of the 314 tools, **222 are
.NET code intelligence** (the Roslyn surface, the debugger, the profiler, Razor/XAML, metacode) and **92
are integrations and plumbing** — Git, GitHub, Docker, the board, build/run wrappers, file and process
handling. Both halves are real; the count is not the reason to install it. These are:

- **715 files and 123,786 lines** audited in one call, with a project graph, complexity and the first
  move named.
- **87 / 126** Microsoft code-style ids rewritten, each verified against the compiler; the other 39 are
  refused with a written reason instead of being ignored.
- **~800 inspection ids** from five analyzer packs, each with its own code fix where the analyzer ships
  one.
- **60+ refactorings** that update every call site, not only the declaration.
- **28 Razor rules** and **9 XAML rules**, with file:line and a fix.
- A debugger (16 tools over DAP), a profiler (22 tools over EventPipe/ClrMD) and a decompiler
  (ILSpy + Ghidra) that need no external tool on the PATH.

The full catalogue, one line per tool, is [docs/public/tools.md](docs/public/tools.md); the arguments
that are easy to get wrong are in
[docs/public/reference/tool-arguments.md](docs/public/reference/tool-arguments.md).

## Quick start

Requirements: **Windows 10/11** (primary platform) and the **.NET 10 SDK**. Visual Studio is not
required.

```powershell
git clone https://github.com/matteofabbri/VisualMCP.git C:\REPOSITORY\VisualMCP
powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope all
```

The script builds a Release binary, publishes it under your user profile and registers the server
in ZCode and Claude Code. **Restart the client**, open a folder containing a `.sln` or `.slnx`, and
ask:

> Audit this solution and tell me where to start.

The solution is discovered and loaded on demand — no `load_solution` step is needed. Scopes,
verification, upgrade and uninstall: [Getting started](docs/public/getting-started.md). What the
scripts do and which files they touch: [scripts/README.md](scripts/README.md).

## What it does not do

Stated here rather than discovered late. The full inventory is
[Limits](docs/public/reference/limits.md) and [docs/gaps.md](docs/gaps.md).

- **C# is the only language with a complete surface.** Visual Basic has a style layer (15 ids) and the
  build/test tools. Razor and XAML are markup scanners, not compilers: bindings and tag helpers are not
  validated. F#, JavaScript, SQL and YAML are text — searchable and indexed, not understood.
- **Static analysis and process instrumentation, nothing more.** Reflection-only code can look dead,
  source generators are outside the model, dynamic dispatch is not followed. No tool proves a change
  preserves behaviour, and none replaces your test suite.
- **No daemon.** Every call analyses on demand; there is no background pass that squiggles between
  calls. A whole-solution audit is cached per unchanged solution, which is what keeps the second call
  fast.
- **Local by construction.** Tools resolve paths on the local filesystem; the stdio transport is
  intentionally local-only.

## Documentation, by what you need

| If you want to… | Read |
|---|---|
| install, verify, upgrade, uninstall | [Getting started](docs/public/getting-started.md) |
| understand the process, the store, the dashboard, privacy | [How it works](docs/public/how-it-works.md) |
| look up a tool | [Tool catalogue](docs/public/tools.md) · [Tool arguments](docs/public/reference/tool-arguments.md) |
| read and analyse code | [Reading and analysing code](docs/public/guides/code.md) |
| refactor safely, apply style fixes | [Refactoring and style](docs/public/guides/refactoring.md) |
| work on Razor, metacode or decompilation | [Razor, metacode and decompilation](docs/public/guides/razor-metacode.md) |
| build, test, manage NuGet, modernize | [Build, test, packages and modernization](docs/public/guides/runtime.md) |
| profile a live process | [Profiling](docs/public/guides/profiling.md) |
| use `run_plan`, planning, Git, GitHub, Docker | [Workflow and integrations](docs/public/guides/workflow.md) |
| configure ports, paths and environment variables | [Configuration reference](docs/public/reference/configuration.md) |
| know what it does **not** do | [Limits](docs/public/reference/limits.md) |
| troubleshoot | [Help and FAQ](docs/public/help.md) |
| contribute | [CONTRIBUTING.md](CONTRIBUTING.md) |

The engineering notes behind these pages — engine state, gaps, metacode model, profiling internals,
dashboard API, adding a language — are indexed in [docs/README.md](docs/README.md).

## Repository, folder by folder

| Folder | README | What lives there |
|---|---|---|
| `SNITCH/` | [SNITCH/README.md](SNITCH/README.md) | the engine: environment, services, solution store, conventions |
| `SNITCH/Languages/` | [Languages/README.md](SNITCH/Languages/README.md) | one layer per language: C#, VB, Razor, XAML, the neutral style core, the compiler gate |
| `SNITCH/Libraries/` | [Libraries/README.md](SNITCH/Libraries/README.md) | `Snitch.Metacode` and `Snitch.Profiling`, the two host-agnostic libraries |
| `src/` | [src/README.md](src/README.md) | the five-project solution and how to build it |
| `src/VisualMCP/` | [VisualMCP/README.md](src/VisualMCP/README.md) | the MCP host: tool surface, startup order, dashboard, logging, `run_plan` |
| `src/VisualMCP.Tests/` | [VisualMCP.Tests/README.md](src/VisualMCP.Tests/README.md) | the xUnit suite and its helpers |
| `scripts/` | [scripts/README.md](scripts/README.md) | install, reload, routing guard and the three analysers |
| `hooks/` | [hooks/README.md](hooks/README.md) | the `PreToolUse` routing guard that stops `grep`/`sed`/`ls` on .NET repos |
| `skills/` | [skills/README.md](skills/README.md) | the optional `/vs-init` and `/vs-load` client skills |
| `docs/` | [docs/README.md](docs/README.md) | engineering notes; the user guide is in `docs/public/` |
| `samples/` | [samples/README.md](samples/README.md) | runnable demonstrations, starting with the Visual Basic `VbDemo` |

Root files worth knowing: `VisualMCP.sln` (five projects), `CAPABILITY_MAP.md` (the rule-by-rule
coverage table), `CONTRIBUTING.md` (architecture rules and CLA), `LICENSE`. `install.ps1` and
`install.bat` at the root are thin wrappers over `scripts/install.ps1`.

## How the pieces fit

| Layer | Project | Depends on | Responsibility |
|---|---|---|---|
| MCP host | `src/VisualMCP` | the engine, the two libraries, ModelContextProtocol | stdio transport, tool declarations, `ServerInstructions`, dashboard, logging, self-update |
| Engine | `SNITCH/Snitch` | Roslyn, MSBuildWorkspace, SQLite | loads solutions, navigation/analysis/refactoring/style, and the non-language services (git, docker, execution, planning) |
| Metacode | `SNITCH/Libraries/Snitch.Metacode` | Roslyn syntax, ILSpy, Ghidra | pseudocode model, decompilation, persisted metacode and symbol indexes |
| Profiling | `SNITCH/Libraries/Snitch.Profiling` | ClrMD, TraceEvent | EventPipe traces, heap snapshots, verdicts |
| Store | `<solution>/.visualMCP/visualmcp.db` | SQLite | derived data only: indexes, package facts, logs, board, edit history |

The host is thin on purpose: a tool binds arguments and forwards to one engine service. The engine
does not know what MCP is, and the two libraries do not need a loaded solution — which is what
makes them testable on their own. [SNITCH/README.md](SNITCH/README.md) has the call flow and the
conventions (result records, compiler gate, edit history, no-lock policy).

## Privacy, stated precisely

VisualMCP is a local executable. Your client starts it as a child process and speaks MCP over
stdin/stdout. It opens the solution from disk, keeps derived data in
`<solution>/.visualMCP/visualmcp.db`, and optionally serves a read-only dashboard on a loopback
port. There is no hosted service, no account and no inbound port apart from that dashboard.

The server does not upload source code, telemetry or usage data. It opens the network only when a
specific tool requires it: NuGet restore during a build, package or template upgrades,
`download_file`, the GitHub CLI, PDB downloads, the update check, or the one-time Ghidra download
when `analyze_native_binary` finds no local installation.

What does leave your machine is the **conversation** your client sends to its model provider, and
that includes tool results — if the model calls `read_file` or `read_symbol`, the source it
receives is part of that conversation. This is true of any MCP tool and is controlled by your
client, not by VisualMCP. The full model is in
[How it works → Privacy](docs/public/how-it-works.md#privacy).

## Development

```powershell
dotnet build VisualMCP.sln                                    # engine + host + tests
dotnet test  src/VisualMCP.Tests/VisualMCP.Tests.csproj       # the suite
powershell -ExecutionPolicy Bypass -File scripts\reload.ps1   # rebuild + republish the server
```

`reload.ps1` stops the running server first, because it holds the DLL/exe; the next tool call
spawns the new build. After adding or removing a tool, reconnect the MCP server so the client
refreshes its cached tool list. Architecture rules, the one-type-per-file rule and the CLA are in
[CONTRIBUTING.md](CONTRIBUTING.md); the project layout is in [src/README.md](src/README.md).

The MCP server communicates over stdio — there is no HTTP endpoint to test directly. Use a client
with the registration above, or write an integration test that sends JSON-RPC over stdin. A
Visual Studio build also refreshes the installed copies through a post-build target; set
`VisualMCPInstallOnBuild=false` to opt out.

## Status

- **Platform:** Windows is the primary, fully supported platform. CI also builds self-contained
  binaries for Linux x64 and macOS arm64.
- **Scope:** everything is static analysis or process instrumentation. See
  [Limits](docs/public/reference/limits.md) for the per-language and per-feature boundary.

## License

VisualMCP is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ **Free for noncommercial use** — individuals, hobby and personal projects, learning,
  research, education, and other noncommercial organizations may use, modify and share it at no
  cost.
- 💼 **Commercial / company use requires a paid license.** Any use by or for a company (or for
  commercial advantage or monetary compensation) needs a separate commercial license. Contact
  **me@matteofabbri.org**.

This is **source-available**, not OSI "open source": you can read, fork and contribute, but
commercial use is reserved. See [`LICENSE`](LICENSE) for the exact terms.

*This summary is for convenience only and is not legal advice; the [`LICENSE`](LICENSE) file governs.*
