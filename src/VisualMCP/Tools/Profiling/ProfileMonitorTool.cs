using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling.Monitor;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileMonitorTool
{
    [McpServerTool(Name = "profile_monitor"), Description(
        "Run and probe the diagnostics sidecar — now native and in-process, with no dotnet-monitor binary and no " +
        "child process. action='start' opens a loopback HTTP listener (default http://127.0.0.1:52323; pass port 0 " +
        "to let the OS choose) whose routes are answered by the same EventPipe/ClrMD services the MCP tools use: " +
        "/info, /processes, /metrics?pid=&durationSeconds=&format=json|prometheus, /trace?pid=&profile=&durationSeconds=, " +
        "/dump?pid=&type=, /heap?pid=, /stacks?pid=, /fileio?pid=&durationSeconds= and /artifact?path=. action='probe' " +
        "performs an HTTP GET against one route; action='status' reports whether the listener is alive; action='stop' " +
        "closes it. Requests are bounded windows, not continuous streams. No authentication: the listener is " +
        "loopback-only, so do not expose the port. For one-shot profiling the direct tools (profile_collect, " +
        "profile_counters, profile_stacks, profile_heap, profile_file_io) are equivalent and cheaper.")]
    public static async Task<object> Monitor(
        IDiagnosticsMonitorService monitor,
        IRoslynWorkspace workspace,
        [Description("'status' (default), 'start', 'stop' or 'probe'.")] string action = "status",
        [Description("Loopback URL to listen on (default http://127.0.0.1:52323) or to probe.")] string? url = null,
        [Description("Endpoint path for action='probe', e.g. /info, /processes, /metrics?pid=1234&durationSeconds=5, /fileio?pid=1234.")] string path = "/info")
    {
        try
        {
            var directory = ProfilingToolHelpers.SolutionDirectory(workspace);
            switch (action.Trim().ToLowerInvariant())
            {
                case "start":
                    return await monitor.StartAsync(directory, url);

                case "stop":
                    return monitor.Stop();

                case "probe":
                    return await monitor.ProbeAsync(url, path);

                case "status":
                    return await monitor.StatusAsync(directory);

                default:
                    return new { error = $"Unknown action '{action}'. Use status, start, stop or probe." };
            }
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
