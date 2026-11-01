# VisualMCP

**Your agent is fluent in JavaScript. It guesses at C#.**

C# punishes guessing. Overloads, overrides, generics, partial types, interface dispatch — text search
misses all of them, so your AI reads the wrong thing, renames the wrong symbol, and breaks the build two
hours later. You already know the answer to this problem: it is called an IDE. VisualMCP gives the
agent the IDE's brain — Roslyn's semantic model, the real MSBuild project graph, a debugger, a profiler,
a decompiler — as **420 tools in one local process** speaking MCP to Claude Code, ZCode, or any MCP
client.

**Stop your agent from guessing. Give it a compiler.**

## Start in two minutes

```powershell
git clone https://github.com/matteofabbri/VisualMCP.git C:\REPOSITORY\VisualMCP
powershell -ExecutionPolicy Bypass -File C:\REPOSITORY\VisualMCP\scripts\install.ps1 -Scope all
```

```sh
# Linux / macOS
git clone https://github.com/matteofabbri/VisualMCP.git ~/REPOSITORY/VisualMCP
~/REPOSITORY/VisualMCP/scripts/install.sh --scope all
```

Restart your client, open any folder with a `.sln`, and say the sentence that changes everything:

> **Audit this solution and tell me where to start.**

## See it think

One call, on our own codebase. This is the answer, not a promise about the answer:

```text
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
  Snitch                   → (leaf)
  Snitch.Metacode          → Snitch
  Snitch.Profiling         → Snitch, Snitch.Profiling.Probe
  Snitch.Profiling.Probe   → (leaf, netstandard2.0)
  VisualMCP                → Snitch, Snitch.Metacode, Snitch.Profiling
  VisualMCP.Tests          → Snitch, Snitch.Metacode, Snitch.Profiling, VisualMCP

Suggested first moves:
  1. Extract from 'MoveInstanceMethod(7 args)' (complexity 62) — extract_method on its worst block.
```

It did not stop at "here is your codebase". It ranked it, judged it, and told the agent what to do
first. The next call ranks **1,426 methods** and names the exact decision points that own each score.
The one after that joins git history with complexity and says which file a refactoring pays for first.

## What it gives your agent

- **It reads like a compiler, not like grep.** `find_references` resolves overloads, overrides,
  generics and interface dispatch. `read_symbol` returns the declaration's real source in one call.
  Same-named unrelated text is never touched, comments never lie to it.
- **It changes code and survives the compiler.** 60+ refactorings — rename, extract method and class,
  change signature, move type — each one updates **every call site**, is previewed by default, and is
  refused the moment it would add a compiler error.
- **It runs the whole loop.** Build, run, test with coverage, debug through a real debugger
  (breakpoints, watch, evaluate, sessions that survive between calls), benchmark, and profile a live
  process — CPU hotspots down to the exact line, allocations to the allocating site, locks, I/O waits,
  GC pressure — then read a plain-language verdict instead of a table of numbers.
- **It reads code without source.** Decompile any assembly to C# with ILSpy, work inside it
  (search, references, outline), recover native binaries with Ghidra — all from a prompt, nothing to
  install beyond the SDK.

## Safety is the product

You do not hand write access to an AI because it has 420 tools. You hand it over because of four
invariants that hold across every one of them:

- **Semantic, not textual.** Every write is a Roslyn operation on the real project, never a regex over
  files.
- **Compiler-gated.** An edit that adds a compiler error is refused. The whole run rolls back if the
  fixes together would break the build.
- **Recorded and revertible.** Every file the server writes lands in the solution's edit history with
  its before-and-after hash; `revert_edit` undoes it, and the revert is recorded too.
- **Read-only where it says so.** Analysis and profiling never modify anything; the mutating tools are
  marked ✎ and most default to a dry run.

This is not a promise we make. It is the mechanism, and it is in every tool.

## It fixes the build — it does not just report it

`get_diagnostics` is the Error List. `fix_diagnostics` is the part an IDE does for you: it rewrites the
mechanical compiler errors, under the same gate.

| Error | What it does |
|---|---|
| CS0168, CS0219 | removes a local nothing reads — or keeps the side effect as a discard |
| CS0169 | removes a private field nothing uses |
| CS0067 | removes an event nothing raises |
| CS0105 | removes the duplicated `using` |
| CS0108, CS0114 | marks a hidden member `new` |
| CS0109 | removes a `new` that hides nothing |

## One model, four languages

Metacode is the part nobody else has: one canonical pseudocode model that reads **C# source, Visual
Basic, Razor pages, decompiled .NET assemblies and native binaries** — persisted per declaration, diffable,
generatable back to C#. Two spellings of the same algorithm compare line-by-line across languages.
Your agent can reason about a Ghidra recovery and a Razor page with the same vocabulary it uses for C#.

## The receipts

- **413 tools** — and `capability_report` prints the live count from the running build, so you never
  have to take the README's word for it.
- **972 tests, all green** — the suite that gates every release.
- **97 of 126** Microsoft code-style ids rewritten under the compiler gate; the other 29 are delegated to
  a dedicated tool, not applicable to C#, or a refactoring rather than a rewrite — each named, never
  silently ignored.
- **~800 inspection ids** from five analyzer packs, each with its own code fix where the analyzer
  ships one.
- **28 Razor rules**, **9 XAML rules**, **16 JS/TS rules**, **22 HTML/CSS rules**, **8 YAML rules** and
  **10 F# rules** with file:line and a fix, the security ones carrying their CWE id.
- **123,786 lines** of our own codebase audited in one call: 0 errors, 11 warnings, and the mechanical
  ones fix themselves.

## How it compares

| | grep and file reading | index-based code MCPs | **VisualMCP** |
|---|---|---|---|
| Understands C# | finds the *text* of a symbol | indexes identifiers | **compiles the solution** — overloads, generics, dispatch |
| Renames safely | renames comments and strings too | misses cross-file uses | **every real reference, compiler-gated** |
| Fixes the build | shows you the log tail | reports findings | **fixes the mechanical errors itself** |
| Debugs and profiles | — | — | **a real debugger and a real profiler** |
| Edits you can trust | writes whatever the model says | same | **gated, recorded, revertible** |

## The honest bit

C# and Visual Basic get the semantic model; XAML, Razor, SQL, JavaScript/TypeScript, HTML/CSS, YAML
and F# get a scanner (they are not compilers), each with a rule set that states what it proves — and
JavaScript/TypeScript and F# also get a **lexical** symbol table (`js_symbols`, `fsharp_symbols`: names,
the declaration a name resolves to, its references, no type model); the Node, Docker and Kubernetes
toolchains are wrapped rather than reimplemented, with an offline manifest validator (`k8s_validate`).
HTML and CSS are scanned, not rendered, and F# has no semantic model — on purpose, and stated with the
other boundaries in the [gap inventory](docs/gaps.md). Everything is static analysis or process
instrumentation: no
tool proves a change preserves behaviour, and none replaces your test suite.
And it runs on your machine, locally — no hosted service, no account, no daemon. Your code leaves the
box only in the conversation your client already sends to its model provider.

## Documentation, by what you need

| If you want to… | Read |
|---|---|
| install, verify, upgrade, uninstall | [Getting started](docs/getting-started.md) |
| understand the process, the store, the dashboard, privacy | [How it works](docs/how-it-works.md) |
| look up a tool | [Tool catalogue](docs/tools.md) · [Tool arguments](docs/reference/tool-arguments.md) |
| read and analyse code | [Reading and analysing code](docs/guides/code.md) |
| refactor safely, apply style fixes | [Refactoring and style](docs/guides/refactoring.md) |
| work on Razor, metacode or decompilation | [Razor, metacode and decompilation](docs/guides/razor-metacode.md) |
| build, test, manage NuGet, modernize | [Build, test, packages and modernization](docs/guides/runtime.md) |
| profile a live process | [Profiling](docs/guides/profiling.md) |
| use `run_plan`, planning, Git, GitHub, Docker | [Workflow and integrations](docs/guides/workflow.md) |
| configure ports, paths and environment variables | [Configuration reference](docs/reference/configuration.md) |
| know what it does **not** do | [The gap inventory](docs/gaps.md) and [requirements/guarantees](docs/reference/limits.md) |
| troubleshoot | [Help and FAQ](docs/help.md) |
| contribute | [CONTRIBUTING.md](CONTRIBUTING.md) |

The engineering notes behind these pages are indexed in [docs/README.md](docs/README.md). Every folder
has its own README: the [engine](SNITCH/README.md), [language layers](SNITCH/Languages/README.md),
[libraries](SNITCH/Libraries/README.md), [the solution](src/README.md), [the MCP host](src/VisualMCP/README.md),
[the test suite](src/VisualMCP.Tests/README.md), [scripts](scripts/README.md), [hooks](hooks/README.md),
[client skills](skills/README.md), [docs](docs/README.md) and [samples](samples/README.md).

## Development

```powershell
dotnet build VisualMCP.sln                                    # engine + host + tests
dotnet test  src/VisualMCP.Tests/VisualMCP.Tests.csproj       # the suite
powershell -ExecutionPolicy Bypass -File scripts\reload.ps1   # rebuild + republish the server
```

Architecture rules and the one-type-per-file rule are in [CONTRIBUTING.md](CONTRIBUTING.md). After
adding or removing a tool, reconnect the MCP server so the client refreshes its tool list.

## License

VisualMCP is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

- ✅ **Free for noncommercial use** — individuals, hobby and personal projects, learning, research,
  education, and other noncommercial organizations.
- 💼 **Commercial / company use requires a paid license.** Any use by or for a company (or for
  commercial advantage or monetary compensation) needs a separate commercial license. Contact
  **me@matteofabbri.org**.

Source-available, not OSI "open source": read it, fork it, contribute to it — commercial use is
reserved. See [`LICENSE`](LICENSE) for the exact terms.

*This summary is for convenience only and is not legal advice; the [`LICENSE`](LICENSE) file governs.*
