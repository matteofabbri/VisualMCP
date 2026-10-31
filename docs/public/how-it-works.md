[Docs home](README.md) › **How it works**

# How it works

VisualMCP is a single local process. This page explains what it loads, what it writes, how it decides
that a change is safe, and what does — and does not — leave your machine.

## The process model

Your MCP client starts `VisualMCP.exe` as a child process and speaks MCP over stdin/stdout. The server's
stdout belongs to the protocol, so every log line goes to a file. Nothing is hosted; there is no
listening port except the optional loopback dashboard.

The process does four things:

- **Loads the solution** with Roslyn's `MSBuildWorkspace`, so tools see the project graph, target
  frameworks, references and compiler diagnostics the way an IDE does.
- **Keeps derived data** in `<solution>/.visualMCP/visualmcp.db` — indexes, edit history, the board and
  cached reports. The source files on disk remain the only truth.
- **Serves an optional read-only dashboard** on a loopback port (`http://127.0.0.1:4677` by default).
  Every route is a `GET`; `VISUALMCP_DASHBOARD=off` disables it.
- **Writes its log** to `~/.claude/mcp-servers/VisualMCP/debug.log`.

Network access happens only for the jobs listed under [Privacy](#privacy).

At startup the server waits briefly for the client's first message, then opens the environment in the
background: it finds solutions under the working directory and loads them so the first tool call does
not pay for MSBuild evaluation. A client's discovery probe (`server/discover`, `tools/list`, then exit)
is detected and skips the load entirely.

## Solution discovery and loading

- Tools **auto-discover** the solution in the working directory on first use. You normally do not need
  `load_solution`.
- `list_solutions` scans a folder tree for `.sln`/`.slnx` files.
- `load_solution` targets a specific solution by absolute path, or reloads one.
- `check_solution` reports what is loaded right now: path, project count, and the C# / Visual Basic breakdown.

Loading uses Roslyn's `MSBuildWorkspace`, with `MSBuildLocator` registering the installed .NET SDK. That
means the server sees the solution as the compiler sees it: project references, target frameworks,
conditional compilation, package references, generated project-system metadata. A solution that cannot
restore or evaluate will produce MSBuild diagnostics rather than silently degrading to text search.

The workspace is a snapshot. VisualMCP keeps it in sync with its own writes; after large changes made
outside it (an IDE, a branch switch), call `load_solution` again to refresh the semantic model.

## The tool surface

All tools are declared in `src/VisualMCP/Tools` as `[McpServerTool]` methods and registered by
`WithToolsFromAssembly`. The catalogue is documented in [tools.md](tools.md); the same list is served by
the dashboard at `/tools`, with each argument's type, whether it is required, and its description.

Names are snake_case: `FindReferences` is exposed as `find_references`. In a client the full name is
prefixed, e.g. `mcp__VisualMCP__find_references`.

## The solution store

`<solution>/.visualMCP/visualmcp.db` is a SQLite database scoped to one solution. The rule for what
goes in it: **only derived, reconstructible data, always stamped with the hash or timestamp of its
inputs**. The source on disk remains the only truth.

| Area | Tables | What a row means |
|---|---|---|
| Activity | `command_log`, `applied_edit`, `context` | Every tool call; every file this server wrote (with the previous text, compressed); cached inspection reports and small settings |
| Board | `board_item`, `board_comment`, `board_history` | A kanban card, its comments, its change history |
| Symbol index | `si_document`, `si_symbol`, `si_usage`, `si_metacode` | Declarations, typed usages (call/read/write/…), and one metacode tree per declaration |
| Metacode index | `mc_document`, `mc_tree`, `mc_node`, `source_document` | One pseudocode tree per declaration with its complexity, the complete AST of each declaration node by node (parent link, property slot, position, depth, span, payload), and the file as its own language writes it |
| Full-text index | `ti_document`, `ti_row`, `ti_line` (FTS5) | One row per line, identifiers split into words, BM25 ranking |
| Packages | `package_fact`, `package_dependency` | The resolved NuGet graph from `obj/project.assets.json` |
| Artifacts | `decompiled_artifact`, `native_artifact`, `native_function`, `native_call` | Decompiled assemblies keyed on their MVID; functions and call edges recovered from a native binary |

The tables are created from one **EF Core model** (`SNITCH/Storage/Schema/VisualMcpDbContext.cs`), which is
the schema's only definition: a fresh database and one written by an older build get the same tables, and
opening a store adds whatever that file is missing without touching what is there. The one exception is
`ti_line`, an FTS5 virtual table that a relational model cannot describe, so its DDL is kept in SQL. Reads
and writes stay in tuned SQL on the stores' own connections; EF Core owns the schema, not the hot paths.

Two consequences worth knowing:

- **Indexes are snapshots.** `reference_index` and `text_index` are built incrementally from content
  hashes. Build or refresh them after substantial edits; `reference_index(action=status)` says what is
  stored.
- **The store is a cache, not a backup.** Add `.visualMCP/` to `.gitignore`. Deleting it costs a
  rebuild, not data. The one exception is the board, which is deliberate project data — but it still
  lives there, so treat the folder as per-machine unless you choose to commit it.

## The dashboard

The server hosts a **read-only** web view of itself at **http://127.0.0.1:4677**.

- `/` — process facts, loaded solutions, table counts, latest calls and edits
- `/tables` and `/t/{table}` — one page per table, with filtering, sorting, paging and CSV/JSON export
- `/schema` — columns as SQLite reports them
- `/tools` — the live tool catalogue with every argument
- `/log` — the tail of `debug.log`, refreshed live, filterable by kind

A column fixed on the left of every page holds the navigation and the **commands invoked** — the most
recent tool calls, newest first, each linking to that tool's own calls in the command log.

There is no page that writes: every route is a `GET`. Table names are resolved against `sqlite_master`,
so a name in the URL cannot inject SQL. Response headers include a strict CSP and `no-store`, and blobs
are summarised rather than served.

Port selection is deterministic: **4677**, then the port it last bound (kept as `dashboard.port` in the
store), then an OS-assigned port. A port that cannot be bound is a warning in the log, never a failure
of the MCP session.

| Variable | Effect |
|---|---|
| `VISUALMCP_DASHBOARD=off` (or `0`, `false`, `no`) | Do not start the dashboard |
| `VISUALMCP_DASHBOARD_PORT=<n>` | Change the preferred first port |
| `VISUALMCP_DASHBOARD_HOST=<host>` | Bind another interface. Loopback by default |

## The safety model

Tools fall into four classes. Knowing which one you are calling is the whole safety story.

| Class | Examples | What it does |
|---|---|---|
| **Read-only** | `find_symbol`, `read_symbol`, `get_diagnostics`, `solution_audit`, `inspect_code`, `profile_*`, `git_status` | Reads the solution, the store or a process. Writes nothing. |
| **Preview-first** | `simplify_code` (default `dryRun=true`), `apply_code_fix` (default `dryRun=true`), `razor_edit` (default `dryRun=true`), most refactorings | Returns the proposed change and a line diff. Nothing is written until `dryRun=false`. |
| **Write-through** | `apply_rename`, `change_signature`, `safe_delete`, `generate_members`, `optimize_usings`, `reorder_members`, `git_commit`, package tools | Performs the change. The tool's schema states the default; several of these have no dry run at all. |
| **Destructive, confirmed** | `git_reset` with `mode=hard`, `git_sync` with `force=true`, `docker_prune`, GitHub deletes | Refuses unless `confirm=true` is passed. |

Three further mechanisms make writes auditable and reversible:

- **The compiler gate.** Risky refactorings and `simplify_code` compile the project after the change
  and refuse it if it would add errors; the file is left untouched. Style rules that claim equivalence
  are also compared operation-by-operation against the code they replace; for every other rewrite the
  gate proves *compilation*, not behavioural equivalence — see [Limits](reference/limits.md).
- **The edit history.** Every write goes through the same file writer, which preserves the file's BOM
  and line endings and records the previous text (compressed) in `applied_edit`. `edit_history` lists
  each write with the tool that made it and whether it is still revertible; `revert_edit(id)` restores
  the file exactly, and refuses if the file changed after the edit.
- **The `expect` guard.** `razor_edit` takes the text the target lines must currently hold and refuses
  the splice if the file no longer matches; it also re-scans the page and refuses an edit that would
  unbalance the markup.

`run_command` and `run_plan` are deliberately outside this ladder: `run_command` executes a shell
command with the privileges of the server process, and `run_plan` can call any tool or command. They
are escape hatches, not sandboxes. The routing guard exists to make the semantic tools the default,
not to constrain these two.

## Privacy

The server is local and has no remote endpoint. It does not upload source, telemetry or usage data.

What does reach a remote party is the **conversation** your client sends to its model provider. That
includes tool results — so if the model calls `read_file`, `read_symbol` or `decompile_assembly`, the
text it receives is part of that conversation. This is identical to any other MCP tool or to pasting
code into the chat; VisualMCP cannot opt your client out of it.

Network access happens only when a specific job requires it:

| Tool family | Why it reaches the network |
|---|---|
| `build_project`, `run_tests`, `quick_benchmark`, `run_benchmarks`, NuGet tools | NuGet restore and package metadata |
| `aspire`, `update_framework`, `update_solution` | Template/package downloads and NuGet metadata |
| `gh_*`, `github` | The authenticated GitHub CLI |
| `download_file` | The URL you pass |
| `profile_symbols` | The symbol server you configure |
| `check_for_update`, `self_update` | GitHub Releases |

Everything else operates on local files, the solution store and local processes. `analyze_native_binary`
runs Ghidra locally and downloads it once from GitHub Releases when no installation is found; the
binary never leaves the machine. The recovered functions are also written to the solution's
`native_artifact` / `native_function` / `native_call` tables.

---

Next: [Tool catalogue](tools.md) · [Configuration reference](reference/configuration.md)
