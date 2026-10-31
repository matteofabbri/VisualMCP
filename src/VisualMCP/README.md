# VisualMCP — the MCP host

This project is the executable. It owns the Model Context Protocol transport, the tool surface,
the startup order and the optional web dashboard; the logic behind each tool lives in
[`../../SNITCH/`](../../SNITCH/README.md). `VisualMCP.csproj` is `net10.0`, framework-dependent,
`PublishSingleFile=false` (Roslyn's `MSBuildWorkspace` finds its BuildHost next to the assembly,
which a single-file bundle breaks).

## Layout

| Folder / file | What it owns |
|---|---|
| `Program.cs` | startup: stdout redirection, `MSBuildLocator.RegisterDefaults()`, DI, `ServerInstructions`, MCP server wiring |
| `Tools/<Area>/` | one `[McpServerToolType]` class per file; each method binds arguments and forwards to one Snitch service |
| `Execution/` | `run_plan` (a dependency graph of tool or shell steps executed in-process), the tool invoker, plan contracts |
| `Formatting/` | `EngineJson` (the serializer the engine's result objects are shaped for) and text helpers |
| `Logging/` | `FileLoggerProvider`, request logging, tool-call logging — never stdout |
| `Startup/` | `ProbeDetectingStream` (skips the eager load for `server/discover` probes) and the memory importer |
| `Dashboard/` | the in-process ASP.NET Core web view on loopback |
| `Analysis/` | host-level composition for `capability_report` and `solution_audit` |
| `Update/` | self-update from GitHub Releases |

The tool areas are `CSharp`, `Debugging`, `Docker`, `Execution`, `Git`, `GitHub`, `IO`,
`Kubernetes`, `Maintenance`, `Metacode`, `Modernization`, `Node`, `Planning`, `Profiling`, `Runtime`,
`Scaffolding`, `Search`, `Sql`, `Update`, `VB`, `Web`. `capability_report` prints the live count per area.

## Startup order

These are load-bearing, not style:

1. `Console.SetOut(TextWriter.Null)` runs first, so a stray banner cannot corrupt the stdio protocol.
2. `MSBuildLocator.RegisterDefaults()` runs before any Roslyn/MSBuild type is JIT-compiled.
3. The real work happens in `RunServerAsync`, marked `[MethodImpl(NoInlining)]`, so the JIT cannot
   compile Roslyn types early.
4. `ProbeDetectingStream` reads the first JSON-RPC message: `server/discover` is a discovery probe
   and skips the eager solution load; `initialize` is a real client and loads as before.
5. Opening the environment and starting the dashboard run in the background, so binding a port can
   never delay or fail an MCP session.

## Adding a tool

1. Implement or extend the service in `SNITCH/` (one `I*Service`), and register it in
   `AddSnitch()` / `AddSnitchMetacode()`.
2. Add a class under `Tools/<Area>/` in its own file, named after the class, with
   `[McpServerToolType]` and one `[McpServerTool]` method.
3. Set a stable tool name. Without `Name = …` the method name is the tool name; the exposed
   spelling is lower-snake (`read_symbol`), and `run_plan` resolves the attribute name first, then
   the method name, so both spellings keep working.
4. Return the engine's result as-is, or a small anonymous object shaped for the client. The MCP
   serializer uses web defaults plus `WhenWritingNull`; `EngineJson` is the single place that
   configures it.
5. Add the tool to `ServerInstructions` in `Program.cs`. ZCode in particular does not forward the
   server's own instructions, and the routing paragraph is what makes a model pick the tool over
   `grep` — a tool that is not mentioned there is a tool that will not be called.
6. Add a test in `../VisualMCP.Tests/` when the tool has behaviour beyond forwarding arguments.

## Dashboard

`Dashboard/DashboardServer` hosts a minimal ASP.NET Core app in a second, in-process host on
**http://127.0.0.1:4677** (else the last port in the store, else an OS-assigned one). It serves
the process state, the tool catalogue, the `debug.log` tail and one page per table found in
`sqlite_master`, so new store tables appear without code changes. It binds loopback only, every
route is a `GET`, logging providers are cleared first (stdout belongs to the protocol), and a port
that cannot be bound is a warning, never an MCP failure. `VISUALMCP_DASHBOARD=off` disables it;
`VISUALMCP_DASHBOARD_PORT` / `VISUALMCP_DASHBOARD_HOST` move it.

## Logging

Every request and tool call is appended to
`%USERPROFILE%\.claude\mcp-servers\VisualMCP\debug.log` (fixed path, whichever copy of the exe is
running). `REQ` lines carry method, id, truncated params and duration; `TOOL` lines carry the
arguments and outcome. The dashboard's `/log` page tails the same file. Nothing is written to
stdout.

## run_plan

`Execution/PlanExecutionService` runs a whole dependency graph of steps in-process: `tool` steps
call any tool this server exposes, `command` steps run a shell command, `paths` keeps overlapping
writers apart, `forEach` fans out, and the plan is validated (duplicate id, unknown tool, unknown
dependency, cycle) before anything executes. It exists so a mechanical fan-out costs one round trip
instead of one model turn per step.

## Build behaviour that surprises people

`VisualMCP.csproj` has a post-build target that runs `scripts\install.ps1 -Scope all -StopRunning`
when the build starts from Visual Studio — it stops the running server, publishes Release and
re-registers it. Set `VisualMCPInstallOnBuild=false` to opt out. From the command line the target
does nothing; use `scripts\reload.ps1` instead (see [`../../scripts/README.md`](../../scripts/README.md)).
