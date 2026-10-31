# scripts — install, reload, guard and analysis

The installers (PowerShell on Windows, POSIX shell on Linux/macOS), the reload helper, and three
Python analysers. Nothing here runs as part of the server; they are developer and operator tools.

| Script | What it does |
|---|---|
| `install.ps1` | Windows: builds a Release binary, publishes it under the user profile and registers the server in ZCode and/or Claude Code at the chosen scope. Also installs the routing guard unless `-NoHooks` |
| `install.sh` | The same for Linux and macOS (POSIX sh): publish, register, routing guard; `--hooks-only` refreshes the guard without publishing |
| `install-global.ps1` | One-line wrapper for `install.ps1 -Scope all` |
| `install-hooks.ps1` | Windows: installs or removes the `PreToolUse` guard and the `AGENTS.md` block — see [`../hooks/README.md`](../hooks/README.md) |
| `reload.ps1` | Stops every running server, rebuilds Debug and republishes Release, so the next tool call spawns the new build |
| `audit-mcp-usage.py` | Reads ZCode's `model-io-*.jsonl` and lists the turns where C# work used Read/Grep/Bash while VisualMCP was never called |
| `analyze-tool-sequences.py` | Measures call bursts in a session store: which calls chain and which could have been one call |
| `analyze-induced-chains.py` | Finds *induced* calls — a call whose argument name first appeared in the previous call's output (the search→read pattern) |

## install.ps1

```powershell
# ZCode + Claude Code (default)
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope all

# ZCode only
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope zcode

# Claude Code only, global
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope user

# Only this project, private to you
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope local -ProjectDir C:\REPOSITORY\MyApp

# Shared with the team via a committed .mcp.json
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope project -ProjectDir C:\REPOSITORY\MyApp

# Remove the registration at the same scope
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Scope all -Uninstall
```

| Flag | Effect |
|---|---|
| `-Scope all\|zcode\|user\|local\|project` | where the registration goes; `all` is ZCode (user) + Claude Code (user) |
| `-ProjectDir` | the project folder for `local` / `project` |
| `-Uninstall` | removes the registration instead of adding it |
| `-StopRunning` | stops a running server first, so the published files are not locked |
| `-NoHooks` | skips the routing guard |

Published copies: `~/.zcode/mcp-servers/VisualMCP/app/` for ZCode and
`~/.claude/mcp-servers/VisualMCP/app/` for Claude Code (one build, copied) — `%USERPROFILE%` on
Windows, `$HOME` elsewhere. The root `install.ps1` / `install.bat` are thin wrappers; the root
`install.ps1` additionally supports `-SetupProject`, which writes the "use the MCP tools, not grep"
block into a target project's `CLAUDE.md` and `AGENTS.md`.

## install.sh (Linux, macOS)

The POSIX counterpart: same scopes, same config shapes, same routing guard.

```sh
./scripts/install.sh                         # ZCode + Claude Code (default)
./scripts/install.sh --scope zcode           # ZCode only
./scripts/install.sh --scope user            # Claude Code, global
./scripts/install.sh --scope local  --project-dir ~/repo/MyApp
./scripts/install.sh --scope project --project-dir ~/repo/MyApp
./scripts/install.sh --uninstall --scope zcode
./scripts/install.sh --hooks-only            # refresh the guard, no publish
```

| Flag | Effect |
|---|---|
| `-s, --scope all\|zcode\|user\|local\|project` | where the registration goes; `all` is ZCode (user) + Claude Code (user) |
| `-p, --project-dir` | the project folder for `local` / `project` |
| `-u, --uninstall` | removes the registration instead of adding it |
| `--stop-running` | stops a running server first (`pkill -f`), so the published files are not locked |
| `--no-hooks` | skips the routing guard |
| `--hooks-only` | installs or removes only the guard |

It needs `dotnet` (to publish) and, for the ZCode configuration and the routing hook, Python 3: the
config is edited with `json`/`json.dump` so every existing key survives, and the POSIX hook
(`hooks/pre-tool-use.py`) parses the JSON payload it receives. Claude Code is registered through its
own CLI (`claude mcp add`), so that half needs no JSON editing.

**Restart the client after any install or uninstall.** Hooks are read at session start, and the
tool list is cached for the session — a client relaunches the server on the next tool call, but a
new or removed tool needs a reconnect.

## reload.ps1

```powershell
powershell -ExecutionPolicy Bypass -File scripts\reload.ps1          # Debug build + Release publish
powershell -ExecutionPolicy Bypass -File scripts\reload.ps1 -NoPublish  # Debug only
```

It stops the server first because the running process holds the DLL/exe; a plain rebuild fails
with "file in use". After an added or removed tool, reconnect the MCP server so the client
refreshes its cached tool list.

## The Python analysers

They read the agent session store, not the repository. `analyze-tool-sequences.py` and
`analyze-induced-chains.py` take a session database (default: ZCode's SQLite store) and report
where an agent spent extra round trips; `audit-mcp-usage.py` reads the rollout JSONL files.

```powershell
python scripts\audit-mcp-usage.py
python scripts\audit-mcp-usage.py --json --include-implementation
python scripts\analyze-tool-sequences.py
python scripts\analyze-induced-chains.py
```

Their findings are what several read-side tools (`search_code`, `read_files`, `read_tree`,
`solution_map`) were built to answer. The method and the measured numbers are in
[`../docs/agent-ergonomics.md`](../docs/agent-ergonomics.md).
