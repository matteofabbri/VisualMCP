using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileRetentionTool
{
    [McpServerTool(Name = "profile_retention"), Description(
        "Answer 'why is this type still in memory?' — the question a heap inventory cannot answer. It walks the " +
        "managed heap of a live process (paused only while its memory is copied) or of a dump from the GC roots, " +
        "breadth-first, and returns: the root chains that reach instances of the focus type (each frame names the " +
        "field of its parent that holds it, e.g. 'static MyApp.Cache.Root → Dictionary`2 [_entries] → Entry[] [element]'), " +
        "and the types that hold references to those instances. The first path is the shortest, so the last field " +
        "before the target is the link to break. focusType is the CLR type name, matched best-first: the full name, " +
        "then a namespace/nested-type segment ('Order' finds MyApp.Order and Outer+Order), then a case-insensitive " +
        "substring — so the containers that merely mention the type (List<Order>, Order[]) do not become the " +
        "instances under investigation. Omit it to analyse the " +
        "largest type in the heap. Use it after profile_heap shows that one type dominates, when you suspect a cache " +
        "without eviction, a static collection or a GCHandle that is never freed. The search is bounded (maxNodes, and " +
        "includeReferrers=false skips the expensive second pass) and says so when a budget cut it short: 'not found' is " +
        "never reported as 'not reachable'.")]
    public static async Task<object> Retention(
        IRetentionService retention,
        IProfiledProcessService processes,
        [Description("Target process id. Omit if processName or dumpPath is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("Analyse a dump file instead of a live process.")] string? dumpPath = null,
        [Description("Substring of the CLR type name to investigate, e.g. 'MyApp.Order' or 'System.Byte[]'. Omit for the largest type.")] string? focusType = null,
        [Description("How many distinct root chains to return (1-20).")] int maxPaths = 3,
        [Description("Node budget for the root search (guards the walk time; 1000-2000000).")] int maxNodes = 250_000,
        [Description("Also report which types hold references to the instances (a second full heap pass; default true).")] bool includeReferrers = true)
    {
        try
        {
            int? pid = processId;
            if (string.IsNullOrWhiteSpace(dumpPath))
            {
                var (target, error) = processes.Resolve(processId, processName);
                if (target is null)
                    return new { error };
                pid = target.ProcessId;
            }

            var report = await retention.AnalyzeAsync(
                pid, dumpPath, focusType, maxPaths, maxNodes, includeReferrers);

            return new { retention = report, findings = report.Findings, nextSteps = report.NextSteps };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
