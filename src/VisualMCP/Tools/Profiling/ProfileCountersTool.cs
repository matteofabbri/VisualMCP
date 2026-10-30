using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Runtime;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileCountersTool
{
    [McpServerTool(Name = "profile_counters"), Description(
        "Poll EventCounters from a live .NET process for a few seconds and return the time series — the native " +
        "equivalent of 'dotnet-counters collect', with no external process. Default providers cover System.Runtime " +
        "(cpu-usage, working-set, gc-heap-size, alloc-rate, gen-0/1/2-gc-count, time-in-gc, threadpool-queue-length, " +
        "threadpool-thread-count, exception-count, monitor-lock-contention-count, loh-size), ASP.NET Core, Kestrel, " +
        "System.Net.Http/Sockets and gRPC; unknown providers are ignored by the runtime. Each counter comes back with " +
        "min/max/mean/last plus the raw points, which is what you want to confirm a CPU or GC problem before " +
        "collecting a full trace. Use the 'counters' filter to keep the response small.")]
    public static async Task<object> Counters(
        ICounterCollectionService counters,
        IProfiledProcessService processes,
        [Description("Target process id. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("How long to sample, in seconds.")] int durationSeconds = 10,
        [Description("EventCounter interval in seconds (1-10).")] int intervalSeconds = 1,
        [Description("Comma-separated counter-name filter, e.g. 'cpu-usage,gc-heap-size,alloc-rate'. Omit for all.")] string? countersFilter = null,
        [Description("Comma-separated provider names to override the defaults, e.g. 'System.Runtime,My.Counters'.")] string? providers = null)
    {
        try
        {
            var (target, error) = processes.Resolve(processId, processName);
            if (target is null)
                return new { error };

            var filter = Split(countersFilter);
            var providerList = Split(providers);

            var result = await counters.CollectAsync(
                target.ProcessId, filter, durationSeconds, intervalSeconds, providerList);

            return result;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }

    private static IReadOnlyList<string>? Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
