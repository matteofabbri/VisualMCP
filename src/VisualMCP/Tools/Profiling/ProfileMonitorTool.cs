using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling.Monitor;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileMonitorTool
{
    [McpServerTool(Name = "profile_monitor"), Description(
        "Run and inspect dotnet-monitor, the long-running diagnostics sidecar of the seven-tool toolchain. " +
        "action='status' reports whether the tool is installed and whether an instance started by this server is " +
        "alive; action='start' launches 'dotnet-monitor collect --no-auth' on a loopback URL, generating a settings " +
        "file with dump storage and metrics enabled; action='probe' performs an HTTP GET against one of its " +
        "endpoints (default /info, useful ones: /processes, /metrics, /dump); action='stop' kills the instance. " +
        "No auth is used because the listener is loopback-only — do not expose that URL. For one-shot profiling " +
        "prefer profile_collect / profile_counters / profile_stacks / profile_heap, which are native.")]
    public static async Task<object> Monitor(
        IDotnetMonitorService monitor,
        IRoslynWorkspace workspace,
        [Description("'status' (default), 'start', 'stop' or 'probe'.")] string action = "status",
        [Description("Loopback URL to listen on (default http://127.0.0.1:52323) or to probe.")] string? url = null,
        [Description("Optional dotnet-monitor settings.json to use instead of the generated one.")] string? configPath = null,
        [Description("Endpoint path for action='probe', e.g. /info, /processes, /metrics, /dump?processId=1234&type=WithHeap.")] string path = "/info")
    {
        try
        {
            var directory = ProfilingToolHelpers.SolutionDirectory(workspace);
            switch (action.Trim().ToLowerInvariant())
            {
                case "start":
                    return await monitor.StartAsync(directory, url, configPath);

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
