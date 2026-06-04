using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class RunProjectTool
{
    [McpServerTool(Name = "run_project"), Description(
        "When you need to actually run a project (launch the app), use this to execute 'dotnet run' " +
        "on a runnable project in the loaded solution and capture its output, exit code and duration. " +
        "It enforces a timeout and then stops the process, so long-running apps (e.g. a web server or a " +
        "service) return their startup output (such as the listening URL) instead of blocking forever. " +
        "If no project name is given it auto-selects the single executable project, or lists the options " +
        "when there is more than one. The working-directory solution auto-loads on first use.")]
    public static Task<object> RunProject(
        [Description("Optional: name of the project to run. If omitted, the single runnable (executable) project is used.")] string? projectName = null,
        [Description("Optional: arguments to pass to the application itself (forwarded after '--').")] string? appArgs = null,
        [Description("Seconds to let the app run before it is stopped and output is returned (default: 20). For a one-shot console app that exits on its own, it returns as soon as it exits.")] int timeoutSeconds = 20,
        [Description("Build configuration to run (e.g. 'Debug' or 'Release'). Omit for the dotnet default (Debug).")] string? configuration = null,
        [Description("Skip building before running (pass --no-build). Default: false.")] bool noBuild = false)
        => RunProjectImpl.RunAsync(projectName, appArgs, timeoutSeconds, configuration, noBuild);
}
