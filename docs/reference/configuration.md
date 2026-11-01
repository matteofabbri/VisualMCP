[Docs home](../README.md) › **Configuration reference**

# Configuration reference

Everything VisualMCP writes on disk, every environment variable it reads, and the shape of its client
registrations and dashboard.

## Paths

| What | Where |
|---|---|
| Installed server (ZCode) | `%USERPROFILE%\.zcode\mcp-servers\VisualMCP\app\` |
| Installed server (Claude Code) | `%USERPROFILE%\.claude\mcp-servers\VisualMCP\app\` |
| ZCode configuration | `%USERPROFILE%\.zcode\cli\config.json` → `mcp.servers` (workspace: `<repo>\.zcode\config.json`) |
| Claude Code configuration | `%USERPROFILE%\.claude.json`, or `.mcp.json` in the project |
| Client routing rules | `%USERPROFILE%\.zcode\AGENTS.md`, and `CLAUDE.md`/`AGENTS.md` in a project |
| Log | `%USERPROFILE%\.claude\mcp-servers\VisualMCP\debug.log` |
| Solution store | `<solution>\.visualMCP\visualmcp.db` |
| Disk policy | `<solution>\.visualMCP\disk-policy.json` (written by `disk_usage action=guard`; read at startup) |
| Profiling artifacts | `<solution>\.visualMCP\profiling\` (traces, dumps, reports, symbols) |
| Project memory | `%USERPROFILE%\.claude\projects\<slug>\memory\` |
| Hooks | `hooks\pre-tool-use.ps1` and `hooks\agents-block.md`, installed into the client configuration |

The log is shared by every client: the server logs each request (`REQ`) and each tool call (`TOOL`) to
the same file, including calls that were cancelled.

## Environment variables

| Variable | Effect |
|---|---|
| `VISUALMCP_DASHBOARD` | `off`, `0`, `false` or `no` disables the dashboard |
| `VISUALMCP_DASHBOARD_PORT` | Preferred first port (default 4677); still falls back |
| `VISUALMCP_DASHBOARD_HOST` | Bind another interface (default `127.0.0.1`); exposing it on a LAN is a deliberate choice |
| `GHIDRA_INSTALL_DIR` / `GHIDRA_HOME` | Where Ghidra is installed, used by `analyze_native_binary`; `ghidraHome`/`headlessPath` override it. When neither is set, Ghidra is downloaded into `<local data>\VisualMCP\tools` on first use |
| `JAVA_HOME_OVERRIDE` | JDK handed to Ghidra (JDK 21+); `javaHome` overrides it |
| `JADX_HOME` / `JADX_CLI` | Where JADX is installed (`bin/jadx[.bat]` inside), used by `metacoding action=decompile` on Java archives; `jadxHome`/`cliPath` on `JadxOptions` override it. When neither is set, the cross-platform `jadx-<version>.zip` is downloaded into the same `tools` folder on first use. JADX runs on the JDK `JAVA_HOME` points at, or one found under the usual install roots |
| `VISUALMCP_SKIP_INSTALL_ON_BUILD` | Build-time only: stops the Visual Studio post-build target from re-running the installer |
| `VISUALMCP_TEMP` | The root a disk policy redirects to; `disk_usage action=guard` sets it. A policy file found at startup is applied before anything spawns a build |

Standard .NET variables still apply (`NUGET_PACKAGES`, `DOTNET_*`). The server reads one optional file of
its own: `<workspace>\.visualMCP\disk-policy.json`, written by `disk_usage action=guard`. It holds the
root and the variables it sets (`TEMP`, `TMP`, `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH`,
`DOTNET_CLI_HOME`, `DOTNET_BUNDLE_EXTRACT_BASE_DIR`), which are applied to the process and inherited by
every child process. Delete the file, or set `enabled=false`, to stop redirecting. Everything else the
server keeps is the solution store.

## Client registration

### ZCode

`%USERPROFILE%\.zcode\cli\config.json`:

```json
{
  "mcp": {
    "servers": {
      "VisualMCP": {
        "type": "stdio",
        "command": "C:\\Users\\<you>\\.zcode\\mcp-servers\\VisualMCP\\app\\VisualMCP.exe",
        "args": [],
        "timeoutMs": 60000
      }
    }
  }
}
```

ZCode's schema is strict: an unknown key drops the server, and the JSON must be **BOM-free**. The install
script writes it that way; hand-editing it is where most "server not found" problems start.

### Claude Code / generic MCP client

```json
{
  "mcpServers": {
    "VisualMCP": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["C:\\REPOSITORY\\VisualMCP\\src\\VisualMCP\\bin\\Debug\\net10.0\\VisualMCP.dll"]
    }
  }
}
```

For a portable, team-shared setup prefer the DLL form above over the installed `.exe`: the executable
path is machine-specific.

### Client instructions

MCP servers can send `instructions` with the handshake. Clients that surface them (Claude Code does)
receive a short routing guide automatically. ZCode does not forward them, which is why the installer
also writes the routing block into `AGENTS.md`. If you manage the configuration yourself, copy
`hooks\agents-block.md` into your client's instruction file.

## Dashboard

Runs inside the MCP process, loopback only, read-only.

### Port policy

1. **4677** (`VISUALMCP_DASHBOARD_PORT` changes the first choice).
2. The port it last bound, stored as `dashboard.port` in the store's `context` table.
3. An OS-assigned port.

Whatever it binds is written back, so the next start reuses it. A second instance on the same solution
therefore gets its own stable port instead of colliding. If every candidate fails, the dashboard logs a
warning and the MCP session continues.

### Pages

| Page | Shows |
|---|---|
| `/` | Process facts (pid, uptime, memory, runtime, transport, working directory, log path), loaded solutions, table counts, latest calls and edits |
| `/tables` | Every table in the store, grouped by area, with row and column counts |
| `/t/{table}` | One page per table: substring filter, sortable headers, paging, CSV/JSON export |
| `/schema` | Columns of every table as SQLite reports them |
| `/tools` | The live tool catalogue: name, read-only/destructive hint, description, every argument |
| `/log` | Tail of `debug.log`, newest first, refreshed every 2.5 s; filter by text and kind |

Tables are discovered from `sqlite_master`, so a table added by a future feature gets a page
immediately. FTS5 shadow tables are hidden.

### Query parameters

`/t/{table}`: `q` (substring over every non-blob column), `page`, `size` (1–1000, default 100), `sort`,
`dir` (`asc`/`desc`), `format` (`html`, `csv`, `json`).

`/log`: `q`, `lines` (20–3000), `only` (`tool`, `req`, `err`, `warn`), `fragment=1` (feed only),
`live=0` (no auto-refresh).

### JSON API

| Endpoint | Answer |
|---|---|
| `GET /api/health` | `ok`, pid, port, url, status, started-at, uptime, database path |
| `GET /api/overview` | Process facts, dashboard state, database path and size, per-table row counts |
| `GET /api/tables` | Every table: name, kind, rows, columns, title, area, description |
| `GET /api/tables/{table}` | One page of rows plus the column list |

Blobs are summarised (`<blob N bytes>`), never served.

## The solution store

`<solution>/.visualMCP/visualmcp.db` — SQLite, scoped to one solution. Only derived, reconstructible data
with a freshness key goes in it; the source on disk stays the only truth.

| Area | Tables | Contents |
|---|---|---|
| Activity | `command_log`, `applied_edit`, `context` | Tool-call log; file writes with the previous text; cached inspection reports and settings such as `dashboard.port` |
| Board | `board_item`, `board_comment`, `board_history` | Kanban cards, comments, change history |
| Symbol index | `si_document`, `si_symbol`, `si_usage`, `si_metacode` | Documents with content hashes, declarations, typed usages, one metacode tree per declaration |
| Metacode index | `mc_document`, `mc_tree`, `mc_node`, `source_document` | Pseudocode trees with their complexity, the complete AST of each declaration as one row per node, and each file as its own language writes it. Every AST row records `source_path` (the file, or the binary for decompiled code) and whether it was decompiled; a text file with no frontend is one `File` node |
| Full-text index | `ti_document`, `ti_row`, `ti_line` (FTS5) | One row per line, identifiers split into words, BM25 ranking |
| Packages | `package_fact`, `package_dependency` | Resolved NuGet graph from `obj/project.assets.json` |
| Artifacts | `decompiled_artifact`, `native_artifact`, `native_function`, `native_call` | Decompiled assemblies keyed on MVID; functions and call edges recovered from native binaries |

The schema is defined once as an EF Core model (`SNITCH/Storage/Schema/VisualMcpDbContext.cs`) and created
from it: opening a store adds the tables and indexes that file is missing, and creates no columns. The FTS5
virtual table `ti_line` is the one shape the model cannot describe and stays raw SQL in `VisualMcpSchema`.

Add `.visualMCP/` to `.gitignore`: it is a per-machine cache. Deleting it costs a rebuild of the indexes,
not data — except the board, which is deliberate project data stored in the same file.

## Startup behaviour

- The server's **stdout is the MCP transport**. All logging goes to the file; nothing is printed.
- `MSBuildLocator` registers the installed SDK before Roslyn types are compiled.
- On the first client message the server starts the dashboard and opens the solutions under the working
  directory in a background task. A discovery probe is detected and skips both.
- Tools also open the environment on demand, so a slow eager load is a latency detail, not a correctness
  requirement.

## Build-time configuration

The project file has one automatic behaviour worth knowing: a build started **inside Visual Studio**
re-runs `scripts\install.ps1 -Scope all -StopRunning` after a successful build, refreshing the installed
copy. Set `VisualMCPInstallOnBuild=false` in the project or the environment to opt out, or
`VISUALMCP_SKIP_INSTALL_ON_BUILD=1` for a single build.

---

Next: [Requirements and guarantees](limits.md) · [Help and FAQ](../help.md)
