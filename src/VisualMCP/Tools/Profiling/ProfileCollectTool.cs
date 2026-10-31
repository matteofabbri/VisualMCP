using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Runtime;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileCollectTool
{
    [McpServerTool(Name = "profile_collect"), Description(
        "Collect an EventPipe trace from a live .NET process and write a real .nettrace file — the native equivalent " +
        "of 'dotnet-trace collect' (the external tool does not need to be installed). " +
        "Profiles: 'cpu' (sample profiler stacks + runtime events, the usual first choice), 'gc' (Verbose: GC, heap stats, " +
        "allocation ticks with type names and call stacks), 'exceptions' (throw sites), 'threading' (contention + thread pool), " +
        "'runtime' (general diagnostics), 'diagnose' (Verbose: CPU samples + allocation ticks with stacks + GC/exceptions/contention), " +
        "'async' (System.Threading.Tasks.TplEventSource: task/await events for profile_async), 'database' (EF Core, SqlClient, Npgsql, " +
        "MySql, SQLite, Oracle and Db2 EventSources for profile_database), 'instrumentation' (the probe an assembly rewritten by " +
        "profile_instrumentation writes to), or 'custom' with providers='Name:Keywords:Level,...'. Actions: 'collect' starts, waits durationSeconds and stops (default); " +
        "'start' returns a sessionId for a long-running capture; 'stop' ends that session; 'sessions' lists open ones. " +
        "Feed the resulting .nettrace to profile_analyze for CPU hotspots, allocation sites, GC pauses, exceptions, " +
        "lock contention and JIT time, with file:line when the PDB is available.")]
    public static async Task<object> Collect(
        ITraceCollectionService traces,
        IProfiledProcessService processes,
        [Description("Target process id. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("Preset: 'cpu' (default), 'gc', 'exceptions', 'threading', 'runtime', 'diagnose', 'async', 'database', 'instrumentation' or 'custom'.")] string profile = "cpu",
        [Description("How long to collect when action='collect', in seconds.")] int durationSeconds = 10,
        [Description("Where to write the .nettrace. Default: <solution>/.visualMCP/profiling/trace-<process>-<pid>-<time>.nettrace.")] string? outputPath = null,
        [Description("Only for profile='custom': comma-separated 'Provider:Keywords:Level' entries.")] string? providers = null,
        [Description("EventPipe circular buffer size in MB.")] int bufferMb = 256,
        [Description("Ask the runtime to attach call stacks to events (allocation ticks, exceptions). Needed for allocation-site attribution; the named presets already set it.")] bool requestStackwalk = true,
        [Description("'collect' (default), 'start', 'stop' or 'sessions'.")] string action = "collect",
        [Description("Session id returned by action='start', for action='stop'.")] string? sessionId = null)
    {
        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "sessions":
                {
                    var sessions = traces.ListSessions();
                    return new { count = sessions.Count, sessions };
                }

                case "stop":
                {
                    if (string.IsNullOrWhiteSpace(sessionId))
                        return new { error = "action='stop' needs the sessionId returned by action='start'." };

                    var result = await traces.StopAsync(sessionId);
                    return new { action = "stopped", result, next = $"profile_analyze tracePath='{result.Path}'" };
                }

                case "start":
                {
                    var (target, error) = processes.Resolve(processId, processName);
                    if (target is null)
                        return new { error };

                    var live = traces.Start(target.ProcessId, outputPath, profile, providers, bufferMb, requestStackwalk);
                    return new { action = "started", session = live };
                }

                case "collect":
                {
                    var (target, error) = processes.Resolve(processId, processName);
                    if (target is null)
                        return new { error };

                    var result = await traces.CollectAsync(
                        target.ProcessId, outputPath, profile, providers, durationSeconds, bufferMb, requestStackwalk);

                    return new
                    {
                        action = "collected",
                        result,
                        next = $"profile_analyze tracePath='{result.Path}'",
                    };
                }

                default:
                    return new { error = $"Unknown action '{action}'. Use collect, start, stop or sessions." };
            }
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
