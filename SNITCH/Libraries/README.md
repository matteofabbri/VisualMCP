# Libraries — the host-agnostic parts

Three projects live here. They are separate from `Snitch` on purpose: they take explicit paths,
process ids or text and have no workspace dependency, so a test host or a console application can
use them without a loaded solution.

| Project | Assembly | Used by |
|---|---|---|
| `Snitch.Metacode/` | `Snitch.Metacode` | the seven `metacode_*` tools, decompilation, the solution's metacode index |
| `Snitch.Profiling/` | `Snitch.Profiling` | the 27 `profile_*` tools |
| `Snitch.Profiling.Probe/` | `Snitch.Profiling.Probe` | `netstandard2.0` EventSource the instrumenter injects calls to; copied next to an instrumented assembly |

## Snitch.Metacode

One canonical pseudocode model, several frontends and one emitter. The same algorithm written in
any supported language prints the same body line for line; a construct the model cannot express
becomes `RAW[lang] …` plus a diagnostic (`MC0001`–`MC0008`) instead of silently disappearing.

| Folder | What it owns |
|---|---|
| root | the model (`MetacodeModel`, `MetacodeExpressions`, `MetacodeStatements`), the translator, the rewriter, diagnostics |
| `Frontends/` | C# (Roslyn syntax only), Razor pages/components, canonical pseudocode, Ghidra snapshots; Python and C++ readers are opt-in and read AST JSON dumps, not source |
| `Decompilation/` | ILSpy and the C# compactor used by `decompile_type` / `decompile_assembly` / `decompile_to_metacode`, plus the native (Ghidra) pipeline |
| `Indexing/` | the persisted metacode index (`mc_document` / `mc_tree`, plus the complete AST in `mc_node`) that `metacode_index` builds |
| `Symbols/` | the symbol index (`si_symbol`, `si_usage`, `si_metacode`, `si_document`) behind `reference_index` |
| `Coding/` | `IMetacodeCodingService`: decompile-and-store, rewrite a declaration, insert a member |
| `Generation/` | the C# emitter behind `metacode_generate` |
| `Rendering/` | how a tree is printed as canonical text |
| `Analysis/`, `Passes/` | metrics over the model and the tree passes |

Tools: `metacode_translate`, `metacode_compare`, `decompile_to_metacode`,
`analyze_native_binary`, `metacode_index`, `metacode_generate`, `metacoding`. None of them needs a
loaded solution; `analyze_native_binary` is the only one that starts an external process (Ghidra,
installed on demand). Grammar and limits: [`docs/metacode.md`](../../docs/metacode.md).

## Snitch.Profiling

Everything reads a live .NET process through its diagnostics IPC endpoint or a trace on disk — no
`dotnet-trace`, `dotnet-counters`, `dotnet-gcdump`, `dotnet-dump` or `dotnet-stack` process is
spawned. The external tools are only inspected or installed by `profile_tools`.

| Folder | What it owns |
|---|---|
| `Runtime/` | process discovery, EventPipe trace sessions and their presets (`async`, `database` and `instrumentation` add non-CLR providers), EventCounter polling, managed stack walks |
| `Snapshots/` | ClrMD: heap snapshots, retention/root chains, dump collection and analysis |
| `Analysis/` | trace parsing (ETLX conversion for CPU stacks), allocation attribution, I/O attribution, hot paths and lines, source mapping through portable PDBs; the events-only reader plus the Events Viewer, async (TplEventSource) and database (EF Core/SqlClient/…) analyses; `RootCauseService`, the join of a measurement with the metacode AST through `ICodeStructureIndex` |
| `Instrumentation/` | the IL instrumenter (Mono.Cecil + the probe) and the exact enter/exit analyzer behind `profile_instrumentation` |
| `Verdicts/` | the tools that answer a question instead of printing a table: `profile_slowdown`, `profile_memory_growth`, `profile_threadpool`, `profile_review_change` |
| `Tooling/` | the seven-tool catalogue, detection and installation |
| `Symbols/`, `Monitor/` | dotnet-symbol and dotnet-monitor equivalents |
| `Model/`, `Internal/` | shared records and helpers |

Artifacts land in `<solution>/.visualMCP/profiling/` (traces, dumps, reports, symbols), which is
git-ignored. Usage, what each tool answers and the known limits:
[`docs/profiling.md`](../../docs/profiling.md).

## Why these are separate projects

The engine references Roslyn and the workspace; these two do not need either. Keeping them apart
means the profiling and metacode code can be exercised from a unit test with no `MSBuildWorkspace`
in the process, and the host decides where a solution directory is when a tool needs one.
