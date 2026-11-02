[Docs home](README.md) › **The workspace, the store and multiple sessions**

# The workspace, the store and multiple sessions

This is the engineering note behind two user-facing statements: *one server process serves one
workspace*, and *the solution store belongs to the workspace*. It states the model the code enforces
today, what is shared when a client runs two servers on the same folder, and the failure modes that
follow from both.

The short version:

- The **workspace root** is fixed when the process starts — `--root`, `VISUALMCP_ROOT`, or the folder
  the client launched the server in. Nothing can repoint it.
- `load_solution` accepts only solutions **under that root**. Another repository is another server,
  which the client already starts per session.
- The SQLite store is **one file per workspace**, keyed on the workspace's *primary* solution and
  resolved once. Activating a nested `.sln` does not fork a second database.
- Every server a client starts on the same workspace shares that one file. One metacode watcher is
  elected with a **lease in the store**; every dashboard gets its **own port**.

## 1. The unit of state

| Unit | Lifetime | Who decides it |
|---|---|---|
| Client session | one chat / one MCP connection | the client |
| Server process | one session, stdio 1:1 | the client, from its MCP registration |
| Workspace root | the whole process | `--root` / `VISUALMCP_ROOT` / launch directory, pinned once |
| Active solution | the process | discovery (primary), or `load_solution` inside the root |
| Solution store | the workspace | the primary solution's directory, resolved once |
| Dashboard | the process | its own loopback port, remembered per instance |

The reason the process is the tenant is the protocol: an MCP stdio session carries **no per-call
workspace**. The only workspace signal the server ever receives is the directory the client launched it
in. Everything else — discovery, the store, the board, the edit history, the dashboard — derives from
that one input. A server asked to switch repositories would have to guess, so it refuses instead
(§3).

The client registrations reflect this. Neither the ZCode entry
(`%USERPROFILE%\.zcode\cli\config.json` → `mcp.servers.VisualMCP`) nor the Claude Code entry
(`.mcp.json` in the project, or `%USERPROFILE%\.claude.json`) carries a `cwd`: the child inherits the
client's working directory, which in a project is the project folder. That inheritance is the whole
mechanism. `--root` exists for the hosts that have no project folder — Claude Desktop, an IDE plugin —
not for the normal case.

## 2. Startup, in order

The order matters, because each step assumes the previous one has fixed something.

| # | Step | What it fixes | Where |
|---|---|---|---|
| 1 | `--root` / `VISUALMCP_ROOT` / cwd is resolved; the process changes into it | the workspace root | `Startup/WorkspaceRootOption.cs`, `Program.cs` |
| 2 | The disk policy is read from the root | where builds put temp and NuGet state | `DiskPolicy.ApplyStartupPolicy` |
| 3 | The host is built; the context is **pinned** | the root cannot change for the life of the process | `SnitchContext.PinWorkspace` |
| 4 | The background task waits up to 5 s for the client's first message | a discovery probe exits before paying for a load | `Program.cs` |
| 5 | The store is opened and its path logged | one database, at the primary solution | `VisualMcpStore.EnsureDatabase` |
| 6 | The dashboard binds | a port per instance | `DashboardServer.StartAsync` |
| 7 | Memory import, then the environment opens (discover and load every solution under the root) | the active solution | `SnitchContext.OpenEnvironmentAsync` |
| 8 | The metacode index is built, its watcher starts, the symbol index is built | derived data ready for the first tool call | `MetacodeIndexService`, `SymbolIndexService` |

A **discovery probe** — the `server/discover` + `tools/list` sequence a client sends before it decides
to use a server — returns at step 4. It creates no store, binds no port and loads no solution.

The lines to look for in `debug.log`:

```
Workspace root: <root>, pid <pid>.
Workspace <root>: store <root>\.visualMCP\visualmcp.db, pid <pid>.
Web dashboard on http://127.0.0.1:<port> — <database>
Opened environment: 2/3 solution(s) loaded under <root>. Active: <name>.
```

The store line can say `(no solution in this workspace)`: the root is valid but contains no
`.sln`/`.slnx`, so there is nothing to store facts about (§4).

## 3. The boundary

| Operation | Rule |
|---|---|
| Discover solutions under the root | always allowed; `bin`, `obj`, `.git`, `.vs`, `node_modules` and `packages` are pruned, to 24 levels |
| `list_solutions(rootPath)` | allowed on **any** directory: it is a read-only scan and changes nothing |
| `load_solution(path)` | allowed only for a `.sln`/`.slnx` file **inside the root**; the active solution may be any of them, nested ones included |
| `load_solution(path)` outside the root | refused, with the root and the `--root` value in the error |
| Re-pin the process to another root | refused (`PinWorkspace` and the `WorkingDirectory` setter both throw) |

The refusal is a normal tool result, not an exception:

```
'D:\other-repo\Other.sln' is outside this server's workspace 'D:\repo'. One VisualMCP process
serves one workspace; start a server with --root "D:\other-repo" (or set VISUALMCP_ROOT) for that
solution. list_solutions shows what this server already covers.
```

Two consequences are worth stating plainly:

- **A nested solution is not a second workspace.** `SNITCH/Snitch.sln` under a root that already has
  `VisualMCP.sln` is a solution you may activate; the store, the board and the edit history stay with
  the root's *primary* solution. That is what closes the "two databases for one repository" defect:
  the store path used to follow the **active** solution, so activating the nested one created
  `SNITCH/.visualMCP/visualmcp.db`.
- **The primary solution is the shallowest one**, then the shortest path, then the first by path order.
  If the root sits *above* several repositories and contains no solution of its own, the primary is
  one of those repositories' solutions, and the store lands there. Start the server in the repository
  you mean.

## 4. The store

`<primary solution directory>/.visualMCP/visualmcp.db` — SQLite. The directory is computed once and
cached; `VisualMcpStore.Connection` never reopens it somewhere else. When the workspace has no solution,
the store is null and every operation degrades to a no-op instead of failing a tool.

What lives there, and what survives deleting the file:

| Kind | Tables | If you delete the file |
|---|---|---|
| Board and notes | `board_item`, `board_comment`, `board_history`, `board_memory` | **lost** — deliberate project data |
| Edit and local history | `applied_edit` (with the pre-write text), `file_snapshot` | **lost** — the audit trail and `revert_edit` |
| Command log | `command_log` | **lost** — the audit of what was called and when |
| Metacode, symbols, full text | `mc_*`, `si_*`, `ti_*` | rebuilt on demand or at the next start |
| Packages, artifacts, native | `package_*`, `decompiled_artifact`, `native_*` | rebuilt from `project.assets.json` and the binaries |

The store is therefore a cache for most of its content and the only home of some of it. The
user-facing wording "the store is a cache, not a backup" is true for the indexes; the board, the
history and the command log are not rebuildable.

Writers and locks:

- Six owners write the file on their own connections — the store, the board, the text index, the
  metacode index, the symbol index and the native store. Each sets `PRAGMA busy_timeout=5000`, so a
  concurrent write waits instead of failing. The dashboard reads on a seventh connection with
  `busy_timeout=4000`; it opens the file read-write only so SQLite can roll back a hot journal, and
  issues nothing but `SELECT`s.
- The full-text index additionally sets `PRAGMA journal_mode=WAL` when it opens. Journal mode is a
  property of the file, so after the first text-index open readers do not block the writer.
- The schema is an EF Core model and is **create-only**: `VisualMcpSchema.Ensure` adds missing tables
  and columns to an older file and never drops or rewrites what is there. One column,
  `applied_edit.before_text`, is added in place because the history learned to keep the text after
  the first schema shipped.

Two failure modes are deliberate:

- **No solution under the root.** `check_solution` reports `databasePath: null`; the board and the
  history tools say *"No solution is loaded (or its folder is not writable), so there is no board
  database. Start the server in a folder that contains a .sln/.slnx (or pass --root), so the workspace
  has a store."* Every tool that does not need the store still works.
- **The folder is read-only** (a mounted volume, a protected checkout). The store marks itself
  unavailable and behaves like the previous case; nothing else fails.

Moving or copying a repository moves the folder, and the store with it. The board travels; the
indexes and the edit history are keyed on **absolute file paths**, so they still point at the old
location. Rebuild the indexes (`metacode_index build`, `reference_index build`, `text_index build`) and
expect old `revert_edit` rows to refer to the old path.

## 5. Two servers on one workspace

A client starts one server per session. Two sessions on the same folder therefore mean two processes,
two PIDs and **one store**. What is shared and what is not:

| Shared through the database | Per process |
|---|---|
| Board cards, comments, memory | the Roslyn workspace and the active solution |
| Edit history, local history, command log | the in-memory caches (compilations, symbol data) |
| Metacode, symbol and text indexes | the dashboard listener and its port |
| `context` rows: leases and dashboard ports | the lease *ownership* of the metacode watcher |

The last row is the one piece of coordination: without it, every session would run its own 2-second
metacode watcher over the same files. The protocol is deliberately small:

- A row `lease.<name>` in `context`, value `pid|startTicks|heartbeatTicks`. `startTicks` is the
  process start time, so a recycled PID is not mistaken for the old owner.
- `TryAcquireBackgroundLease` renews when the owner is this process, holds when another **live** owner
  has beaten within the TTL, and steals when the owner is dead or its heartbeat is older than the TTL.
- The metacode watcher uses the lease `metacode-watch` with a 30-second TTL and renews on every tick
  (the loop runs every 2 seconds). `ReleaseBackgroundLease` removes only this process's lease.
- A second server therefore idles its watcher until the owner stops or crashes; failover takes at
  most the TTL.

What the lease does **not** cover, on purpose: the one-time startup builds of the metacode and symbol
indexes, the on-demand index builds, command-log and board writes, and the memory import. Those are
incremental and idempotent, and every process pays for them once; serialising them would make a second
session wait on the first for no correctness gain. Two sessions do mean two warm-ups — bounded by the
indexes' timestamp and hash checks.

The dashboard port is the other per-process resource:

- Each process writes `dashboard.port.<pid>` = `port|pid|startTicks` after it binds.
- On start it tries the preferred port (4677, or `VISUALMCP_DASHBOARD_PORT`), then the ports of
  instances whose process is **no longer running** (newest first), then an OS-assigned port.
- Liveness is the PID plus the process start time, within two seconds: a recycled PID does not keep a
  dead instance's port reserved.
- The row is not deleted on a clean stop; that is what lets a restart reuse its own port. A live
  sibling's row is never a candidate, so the two servers keep separate ports instead of overwriting
  one shared value.

## 6. Invariants

| Invariant | Enforced by |
|---|---|
| One workspace per process | `SnitchContext.PinWorkspace`; the `WorkingDirectory` setter refuses after the pin |
| `load_solution` cannot leave the workspace | `IoService.LoadAsync` + `SnitchContext.ActivateSolutionCoreAsync` |
| The store does not follow the active solution | `VisualMcpStore.Connection` resolves the directory once |
| The store is the primary solution's | `VisualMcpStore.ResolveStoreDirectory` |
| No fact crosses workspaces | one database per workspace, no global store |
| One metacode watcher per workspace | `TryAcquireBackgroundLease("metacode-watch")` |
| One port per live process | `dashboard.port.<pid>` + liveness |
| The store is optional | null on no solution or a read-only folder; tools degrade |

## 7. Failure modes

| Symptom | Cause | What to do |
|---|---|---|
| `load_solution` says *outside this server's workspace* | the server was started in another folder | restart it with `--root "<repo>"`, or start the client in that repo |
| Two `.visualMCP` folders in one repository | an older build followed the **active** solution; a nested `.sln` was activated | the current build writes only the primary solution's store; delete the stale nested folder |
| Board says *no board database* | no solution under the root, or the folder is not writable | start in the folder that holds the `.sln`/`.slnx`, or pass `--root` |
| Dashboard is on an unexpected port | 4677 is taken by another live instance | open the URL from `debug.log` or `GET /api/health`; the port is per instance by design |
| `lease.metacode-watch` names a dead PID | the owner crashed | nothing: the next tick after the 30-second TTL steals it |
| The index is empty after moving the repo | the indexes are keyed on absolute paths | rebuild the index you use |
| An old and a new build run side by side | the old build does not read the lease | expect two watchers until the old process exits |
| The store grew to gigabytes | the metacode AST stores every declaration node | it is a cache: delete it (after exporting the board if you rely on it) |
| Writes from two sessions seem slow | SQLite serialises writers; each waits `busy_timeout` | expected; the alternative is a server, not a file |

## 8. Why not one shared database

The alternative — a single database under the user profile with a workspace column on every table —
was considered and rejected. It does not fix the problem it appears to fix:

- **The protocol has no per-call workspace.** "Which project is calling" would still be derived from
  the process's active solution, which is the bug; a shared file only changes where the wrong row
  lands.
- **The keys collide.** Several tables are not workspace-qualified —
  `package_fact(Project, Id, Tfm)`, `decompiled_artifact(Mvid)`, `context(Key)` — so two repositories
  with the same project name or the same binary would overwrite each other.
- **Every query would need a mandatory predicate.** The stores write SQL by hand; a forgotten
  `WHERE workspace = ?` in `revert_edit` would restore another project's text, and the board would mix
  two projects' cards. A file per workspace makes that mistake impossible.
- **The artifacts already live on disk per repository.** Decompiled output, profiling traces and
  native snapshots are files under `<workspace>/.visualMCP`; only the index would move, so the store
  would still be split.
- **It concentrates writes.** One file would collect the writers of every workspace at once, on top of
  the indexes that are already the largest part of the file.

Cross-project visibility, if it is wanted, is a **read-only** layer: a registry that enumerates the
known `<workspace>/.visualMCP/visualmcp.db` files and a dashboard that reads them. That is a different
feature from the write path, and it does not require merging anything.

## 9. Observing it

- **`check_solution`** returns `workspaceRoot`, `databasePath` and `processId` next to the solution
  facts. It is the fastest way to prove which build is running and which store it uses.
- **The startup log** lines quoted in §2 name the root, the store and the pid.
- **`GET /api/health`** per dashboard returns `pid`, `port` and `database`; **`GET /api/overview`** adds
  `process.workingDirectory` and the table counts.
- **The `context` table** (dashboard → Tables → `context`, or `GET /api/tables/context`) shows the
  coordination rows:

```sql
SELECT "key", "value", "updated_at"
FROM context
WHERE "key" LIKE 'dashboard.port.%' OR "key" LIKE 'lease.%';
```

`dashboard.port.<pid>` says which instance owns which port; `lease.metacode-watch` says which process
is currently indexing.

## 10. Changing this

The code that implements the model, and what a change must keep true:

| Concern | File |
|---|---|
| Root resolution | `src/VisualMCP/Startup/WorkspaceRootOption.cs`, `Program.cs` |
| Pin and boundary checks | `SNITCH/SnitchContext.cs`, `SNITCH/Workspace/WorkspacePath.cs`, `SNITCH/IO/IoService.cs` |
| Store location and lifetime | `SNITCH/Storage/VisualMcpStore.cs` |
| Background lease | `SNITCH/Storage/VisualMcpStore.Lease.cs`, `SNITCH/Libraries/Snitch.Metacode/Indexing/MetacodeIndexService.cs` |
| Dashboard ports | `src/VisualMCP/Dashboard/DashboardPort.cs`, `DashboardDatabase.StoredPorts`, `DashboardServer` |
| The contract as data | `IRoslynWorkspace.WorkspaceRoot`, `CheckSolutionResult` |

- Do not key the store on the **active** solution again. The primary solution is the workspace's
  identity; the active one changes by design.
- A new background pass that writes the store should take a lease first, like the metacode watcher;
  a pass that is cheap and idempotent may skip it, but say so in the code.
- A new process-level resource (a port, a file lock, a pipe) gets a key per PID, not a shared
  last-writer-wins row.
- Keep these tests green: `WorkspaceBoundaryTests` (root pin, outside refusal, store does not move),
  `BackgroundLeaseTests` (renew, steal, release), `DashboardTests` (port policy, per-instance row),
  `StoreTests` and `SchemaTests` (store path and schema).

See also: [How it works](how-it-works.md) for the user-facing summary, [the dashboard](dashboard.md)
for the pages and ports, [the configuration reference](reference/configuration.md) for every path and
variable, and [engine-state](engine-state.md) for what the store holds table by table.
