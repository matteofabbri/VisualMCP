# SNITCH — the code-intelligence engine

`SNITCH/` is the engine behind VisualMCP. Every tool the MCP host exposes is a thin shim over a
service declared here. The library has no dependency on MCP, on a client, or on a command line:
`src/VisualMCP` is one host, and a test project or a console app can be another.

| Piece | File | What it is |
|---|---|---|
| Project | `Snitch.csproj` | net10.0, assembly `Snitch`, namespace `Snitch.*`; excludes `Libraries/**`, which are separate projects |
| Composition root | `SnitchServiceCollectionExtensions.cs` | `AddSnitch()` registers the environment, the solution store and every `I*Service` as a singleton |
| Environment | `SnitchContext.cs` | owns every discovered solution, the *active* one and its docs; implements `IRoslynWorkspace`, the surface feature services depend on |
| Identity | `SnitchOptions.cs` | host application name, process name, download defaults — nothing hard-codes "VisualMCP" |

## How a call flows

1. The client calls a tool over MCP.
2. `src/VisualMCP/Tools/<Area>/…` binds the arguments and calls one `I*Service` method.
3. The service uses the Roslyn `Solution` behind `IRoslynWorkspace`, a subprocess (git, docker,
   dotnet, netcoredbg), or the solution store.
4. The result goes back as a named record, or as a JSON-shaped object the host serialises with
   `VisualMCP/Formatting/EngineJson.cs`.

## Folders

| Folder | What it owns |
|---|---|
| `Workspace/` | `IRoslynWorkspace`, solution discovery, file discovery, project docs |
| `Languages/` | the per-language layers — [Languages/README.md](Languages/README.md) |
| `Libraries/` | the host-agnostic libraries — [Libraries/README.md](Libraries/README.md) |
| `Search/` | `search_text` and the FTS5 full-text index over the solution store |
| `Storage/` | `VisualMcpStore` (SQLite at `<solution>/.visualMCP/visualmcp.db`), the EF Core schema model, command log, edit history |
| `Execution/` | build, publish, pack, run, EF migrations, environment info, `run_command`, `stop_server` |
| `Debugging/` | DAP client, netcoredbg discovery and installation, breakpoints, sessions |
| `Git/`, `GitHub/`, `Docker/` | CLI wrappers that return structured results |
| `Planning/` | the kanban board and the task checklist, stored in the solution store |
| `Platform/` | host system facts, line endings, text diff |
| `Runtime/` | HTTP invoke and SignalR sessions |
| `Scaffolding/` | embedded templates: licences, CONTRIBUTING, CI workflows |
| `Web/` | `download_file` |

## Conventions that matter

**Result contracts.** `IO/` is the model to copy: services return named records (`IoResults.cs`)
with a documented `IoResult.Error` channel, and the JSON shape is covered by tests. The rest of
the engine still returns anonymous objects shaped for the client, serialised by `EngineJson`
with web defaults. New services should return records.

**Compiler gate.** Anything that edits source is verified before it is written: the project is
compiled with the change and the edit is refused when it would add an error. `Languages/Gate/`
(`CompilationCache`, `SemanticEquivalence`) makes that affordable; `RefactoringFileWriter` owns
the write plus the in-memory document update, so the workspace never serves a stale file.

**Edits are auditable.** Every write is recorded in the solution store's edit history with the
previous text compressed and content hashes before and after, which is what `edit_history` and
`revert_edit` read.

**Concurrency.** No `lock` blocks: `System.Collections.Concurrent`, `Interlocked`, or a
`SemaphoreSlim` where a real async gate is needed. `SnitchSolution.TryUpdate` uses an
Interlocked compare-and-swap, so two tools editing at once cannot lose an update.

**Discovery prunes.** `.claude`, `.zcode`, `.idea` and build folders are skipped — a repository
with worktrees used to load the same solution four times. An explicit `load_solution` still
opens such a solution.

**MSBuildLocator belongs to the host.** The engine assumes MSBuild types are registered already:
`src/VisualMCP/Program.cs` calls `MSBuildLocator.RegisterDefaults()` before any Roslyn type is
JIT-compiled. In a host, a `Microsoft.Build.*` package reference must carry
`ExcludeAssets="runtime"` next to the locator, or the locator refuses to start. See
[src/VisualMCP/README.md](../src/VisualMCP/README.md).

**The store is derived data.** `<solution>/.visualMCP/visualmcp.db` holds facts reconstructed
from the tree (indexes, package facts, logs, board, edit history) and never becomes the source of
truth. It is git-ignored.

**The schema is the model, not a string.** Every table of that database — the six stores that own
tables included — is declared once in `Storage/Schema/VisualMcpDbContext.cs`, and
`VisualMcpSchema.Ensure(connection)` creates from it whatever a file is missing. A store that needs a
new table adds an entity, not a `CREATE TABLE`; the FTS5 virtual table (`ti_line`) is the one shape the
relational model cannot express and stays raw SQL in `VisualMcpSchema`.

## Adding a service

1. Declare `I<Something>Service` and implement it in the folder that owns the concern.
2. Register it in `SnitchServiceCollectionExtensions.AddSnitch()`.
3. Expose it with a tool in `src/VisualMCP/Tools/<Area>/` — see the host README.
4. If it edits code, route the write through the compiler gate and the edit history.

Rule coverage, the IDE rule table and what the engine cannot do are documented in
[`docs/engine-state.md`](../docs/engine-state.md), [`CAPABILITY_MAP.md`](../CAPABILITY_MAP.md)
and [`docs/gaps.md`](../docs/gaps.md).
