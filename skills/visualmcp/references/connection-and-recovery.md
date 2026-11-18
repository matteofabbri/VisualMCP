# Connection and recovery

[Skill](../SKILL.md) › connection and recovery

Read this when a VisualMCP tool is missing, blocked or fails. The ladders below are
ordered: take the first step that matches the observed blocker, then stop. Never install
the .NET SDK, Node.js, a JDK or any analysis engine as part of a repair — say what is
missing and stop.

## The tools are absent

No `mcp__VisualMCP__*` tool appears in the session. Three owners, checked in order:

1. **Never registered on this machine.** Run the script installer from a checkout
   (`scripts\install.ps1` on Windows, `scripts/install.sh` elsewhere) with the scope that
   matches the client in use, then restart the client:

   | Scope | Availability | Configuration written |
   |---|---|---|
   | `all` *(default)* | ZCode + Claude Code + the Claude desktop app | both client configs |
   | `zcode` | every ZCode session on this machine | `~/.zcode/cli/config.json` → `mcp.servers.VisualMCP` |
   | `user` | every folder, Claude Code global | Claude Code user configuration |
   | `local` | one project folder, private | Claude Code, keyed to that folder |
   | `project` | one project, shared with the team | `.mcp.json` committed in the project |

   The installer publishes a Release build with `dotnet publish`, registers it, installs
   the routing hook, the AGENTS.md block and this skill, and prints what it changed. It
   does not restart the client.

2. **Registered but the client predates it.** Registration is read at session start:
   restarting the client is the fix, not another install pass. If a restart is impossible
   right now, keep working on parts of the task that do not need the tools and tell the
   user.

3. **Manual registration** (no checkout, or a policy that forbids running the installer):
   add the server to the client's MCP configuration by hand, pointing at a published
   build:

   ```json
   {
     "mcpServers": {
       "VisualMCP": {
         "type": "stdio",
         "command": "C:\\Users\\you\\.zcode\\mcp-servers\\VisualMCP\\app\\VisualMCP.exe"
       }
     }
   }
   ```

   `dotnet <path>\VisualMCP.dll` works on any platform; `dotnet run` does not (its build
   output corrupts the stdio protocol). ZCode's schema is strict: unknown keys drop the
   server, and the config file must be BOM-free.

After connecting, verify in a folder with a solution: `check_solution` reports the loaded
solution, project count and workspace root.

## A tool call reports no solution

`load_solution` is refused outside the workspace, or a tool says no solution was found.
The server pins itself to its launch directory (the `--root` argument or the
`VISUALMCP_ROOT` environment variable) and refuses to load a solution outside it — one
process serves one workspace. Fix the launch, not the call:

- pass an explicit path to `load_solution` when the solution is inside the workspace, or
- restart the server with `--root <repo>` (edit the client registration's `args`) when the
  client launched it somewhere else, or
- call `list_solutions{rootPath}` to see what is discoverable from the current root.

## The hook denies a shell command

The reply names the replacement tool; switch to it. The hook acts only when a
`.sln`/`.slnx` is visible within three parent levels, so non-.NET trees keep their shell.
When a shell form is genuinely required (binary files, counting, complex pipes), call the
MCP tool `run_command` with the same command — it runs locally and stays logged — or add
the literal token `vmcp-allow` to keep the command deliberately. A denied `Write` of a
plan-like `.md` (repository root, `docs/`, a memory store) means the content is a board
card: `create_task_checklist` or `board_remember` writes it instead.

## A call times out or hangs

The client's per-call timeout must outlast the server's own defaults (long operations run
up to 900 seconds). If the client abandoned the call while the server kept working, do not
repeat it unchanged — check the effect first (`git_status`, `edit_history`), then retry
only the part that did not happen. A timed-out test tool call leaves its `dotnet test`
process tree alive: rerun with `rerun_failed` instead of stacking a second run.

## The build is stale

The connected server describes fewer tools than this skill, or a tool misbehaves in a way
a recent commit already fixed: the installed build is older than the code. Re-run the
installer (it republishes; pass `-StopRunning` / `--stop-running` if the executable is
locked), then restart the client. A release build can also update itself from the client:
`check_for_update` reports the latest GitHub release, `self_update` downloads and swaps
the running binary (brief disconnect).

## A plugin install fails to launch

The marketplace plugin registers `VisualMCP` through `scripts/plugin-mcp-launcher.ps1`
(POSIX: `plugin-mcp-launcher.sh`), which requires the published build at the standard
install location. When the launcher exits with "runtime is missing", run the script
installer once, then disable and re-enable the plugin so it relaunches the server. A
plugin registration is separate from the script installers' registrations: removing the
latter does not remove the former, and the reverse.
