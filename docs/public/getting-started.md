[Docs home](README.md) › **Getting started**

# Getting started

VisualMCP installs as a local MCP server. This page covers the supported setup, what the installer
changes on your machine, how to verify the connection, and a first session that exercises the main
families of tools.

## Requirements

| Requirement | Why |
|---|---|
| **.NET 10 SDK** | The server targets `net10.0` and uses MSBuild/Roslyn to load solutions. |
| **Windows 10/11, Linux or macOS** | The server is platform-independent; the installer script is PowerShell (Windows). On Linux/macOS, [register it manually](#manual-installation). |
| **PowerShell 5.1+** | Runs `scripts\install.ps1` (Windows only). |
| An AI client with MCP support | ZCode, Claude Code, or any client that can start a stdio MCP server. |

Visual Studio is **not** required. Optional integrations light up only when their tool is present:
Docker for the `docker_*` tools, the GitHub CLI (`gh`) for `gh_*`, a JDK 21+ for
`analyze_native_binary` (Ghidra itself is downloaded into the per-user tools folder on first use),
and `coverlet.collector` in your test projects for `get_test_coverage_map`.

CI builds and tests on Windows x64, Linux x64 and macOS arm64. The server behaves the same on all
three: `run_command` defaults to PowerShell on Windows and bash elsewhere, and `self_update` downloads
the build for the platform it runs on. The PowerShell installer and routing guard are the Windows part
of the setup.

## Install

From a checkout of the repository:

```powershell
# ZCode + Claude Code (default)
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

### Choose a scope

| Scope | Availability | Configuration written | Use when |
|---|---|---|---|
| `all` *(default)* | ZCode + Claude Code + the Claude desktop app | both client configs | You use more than one client |
| `zcode` | Every ZCode session on this machine | `%USERPROFILE%\.zcode\cli\config.json` → `mcp.servers.VisualMCP` | You only use ZCode |
| `user` | Every folder, global | `%USERPROFILE%\.claude.json` | Claude Code only, everywhere |
| `local` | Only the chosen project folder | `%USERPROFILE%\.claude.json`, keyed to that folder | One project, without affecting others |
| `project` | The chosen project, shared | `.mcp.json` committed in the project | You want teammates to get it |

```powershell
# ZCode only
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode

# Claude Code only, global
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope user

# Just this project, private to you
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope local -ProjectDir C:\REPOSITORY\MyApp

# Shared with the team through a committed .mcp.json
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope project -ProjectDir C:\REPOSITORY\MyApp
```

### What the installer does

1. Publishes a **Release** build of `src/VisualMCP/VisualMCP.csproj`.
2. Copies it to `%USERPROFILE%\.zcode\mcp-servers\VisualMCP\app\` for ZCode and
   `%USERPROFILE%\.claude\mcp-servers\VisualMCP\app\` for Claude Code (one build, copied).
3. Registers the server in the configuration files of the chosen scope.
4. Installs the **routing guard** (see below); skip it with `-NoHooks`.
5. Does **not** restart your client — MCP configuration is read at session start, so restart it yourself.

> **ZCode specifics:** ZCode reads `mcp.servers` from `%USERPROFILE%\.zcode\cli\config.json` (user) or
> `<repo>\.zcode\config.json` (workspace). Its schema is strict — unknown keys drop the server — and the
> file must be **BOM-free**; the script writes it that way. Verify the connection in **Settings → MCP**.
> Tools surface as `mcp__VisualMCP__<tool>`.

> **`project` scope and teams:** the generated `.mcp.json` records this machine's absolute path to the
> published executable. Teammates must run the script once themselves. For a portable team setup, commit
> a `dotnet run`-based `.mcp.json` instead.

### Optional: the routing guard

Registering the server is not enough on its own: a model that has never been told otherwise may keep
doing C# work with `grep -rn`, `sed -n`, `cat` and `ls`. The installer can add two independent pieces
that close that gap:

| Piece | What it does | Where it goes |
|---|---|---|
| `hooks\pre-tool-use.ps1` | Denies read-only shell probes (`grep`, `rg`, `sed -n`, `cat`, `head`, `tail`, `ls`, `find`, `Select-String`, `Get-Content`, `Get-ChildItem`) and shell file writes in a folder with a visible `.sln`/`.slnx`. Replies with the tool to use instead. | ZCode `hooks.events.PreToolUse`; Claude Code `hooks.PreToolUse` |
| `hooks\agents-block.md` | States the routing rules up front, and why C# semantics need Roslyn rather than text matching. | `%USERPROFILE%\.zcode\AGENTS.md` (user scope) |

Build, test, git and non-.NET trees are never touched. Two escape hatches exist for genuine shell
cases: the MCP tool `run_command` (same command, still logged) and the literal token `vmcp-allow` in the
command.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope zcode
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Scope all
powershell -ExecutionPolicy Bypass -File scripts\install-hooks.ps1 -Uninstall

# Skip the guard during an install
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -NoHooks
```

Hooks are read at **session start**: restart the client. The hook never fails a session — an
unparsable payload or an internal error exits 0 (pass).

### Write the routing rules into a project

The root `install.ps1` wrapper adds `-SetupProject`, which writes the same managed block into a target
project's **`CLAUDE.md` and `AGENTS.md`**. Re-running replaces the block instead of duplicating it:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 `
  -SetupProject C:\REPOSITORY\MyApp `
  -SolutionPath C:\REPOSITORY\MyApp\MyApp.sln
```

## Verify the connection

1. Restart the client.
2. In ZCode: **Settings → MCP**, or list the tools in Claude Code. You should see a `VisualMCP` server
   with a large tool list.
3. Open a folder that contains a solution and ask the client to call:

```
check_solution
```

The answer reports whether a solution is loaded, its path, project count, and whether it contains C#.
If no solution is found, pass an explicit path to `load_solution`.

You can also call `list_solutions` with a root folder to see what is discoverable.

## First session

A five-minute tour that touches the main families. Ask the client to run these in order:

| Step | Tool | What to look for |
|---|---|---|
| 1 | `read_project_docs` | The solution's README and Markdown docs, indexed when the solution opened. Use it before reading code. |
| 2 | `solution_audit` | Whole-solution health: inspections grouped by id, compiler diagnostics, complexity hotspots, largest files, project graph and cycles, suggested first moves. |
| 3 | `find_symbol` then `read_symbol` | Locate a type by name, then read its declaration source with line numbers. |
| 4 | `get_diagnostics` | Compiler errors and warnings, equivalent to the IDE Error List. |
| 5 | `simplify_code` with `dryRun=true` on one project | A preview: the before/after pair and a line diff per edit, nothing written. |

Then try a question in your own words:

> Find every implementation of `IOrderService`, show me the one with the most dependencies, and tell me
> whether the interface is used from a background service.

The client should route to `find_implementations`, `get_type_members` / `find_references`, and
`find_callers` rather than grepping.

## Manual installation

If you prefer not to run the script:

```powershell
dotnet build src/VisualMCP/VisualMCP.csproj
```

Then register the built DLL in your client's MCP configuration:

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

Point at the DLL rather than using `dotnet run`: `dotnet run` writes build output, which can corrupt
the stdio protocol. The repository's own `.mcp.json` uses this Debug-DLL form for development; the
installer produces the stable Release build for daily use. On Linux/macOS this manual registration is
the installation path — the same JSON, with the DLL's path on that machine (e.g.
`/home/you/REPOSITORY/VisualMCP/src/VisualMCP/bin/Debug/net10.0/VisualMCP.dll`).

## Upgrade

Two ways:

- **From inside the client:** `check_for_update` reports the latest GitHub release and the installed
  version; `self_update` downloads the matching asset, stages it, and exits the server so a detached
  helper swaps in the new build. The client relaunches it (brief disconnect).
- **From the repository:** pull, then re-run `scripts\install.ps1` with the same scope and `-StopRunning`
  if the executable is locked.

After either, restart the client if it does not reconnect by itself.

## Uninstall

Match the scope you installed with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope all   -Uninstall
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode -Uninstall
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope local -Uninstall -ProjectDir C:\REPOSITORY\MyApp
```

Uninstalling removes the server registration; it does not delete your solution's `.visualMCP` store.
Delete that folder if you also want to remove the indexes, logs, board and edit history for a solution.

---

Next: [How it works](how-it-works.md) · [Tool catalogue](tools.md)
