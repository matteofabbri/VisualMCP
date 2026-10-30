using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Verdicts;
using Snitch.Workspace;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileMemoryGrowthTool
{
    [McpServerTool(Name = "profile_memory_growth"), Description(
        "Answer 'why is memory growing?' with ONE verdict — growth, shrink, stable or single-snapshot — instead of a heap " +
        "table. Compare mode takes two heap JSONs written with profile_heap writeJson=true and names the type the heap grew " +
        "through (instances before → after, MB added); capture mode snapshots a live process, writes the JSON (so it can be " +
        "the next baseline) and answers against baselinePath when one is given. The point is the correlation: without a gc " +
        "trace it can say 'MyApp.Order grew by 120 MB'; with one (tracePath, profile='gc') it adds 'and Orders.cs:88 " +
        "allocates that type', which is the difference between a diff and an actionable line. With a single snapshot the " +
        "verdict is 'single-snapshot' and it explicitly does NOT claim a trend. Use profile_compare for the raw deltas and " +
        "profile_heap for the full type inventory.")]
    public static async Task<object> MemoryGrowth(
        IMemoryGrowthService growth,
        IProfiledProcessService processes,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Compare mode: the earlier heap snapshot JSON (profile_heap writeJson=true).")] string? baselinePath = null,
        [Description("Compare mode: the later heap snapshot JSON. When given, the two files are compared and no process is touched.")]
        string? currentPath = null,
        [Description("Capture mode: target process id. Omit if processName is given.")] int? processId = null,
        [Description("Capture mode: target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("Optional .nettrace (profile='gc') whose allocation sites are correlated with the type that grew.")]
        string? tracePath = null,
        [Description("How many types / allocation sites to return (1-200).")] int top = 25,
        [Description("Resolve allocation sites to file:line using portable PDBs (default true).")] bool resolveSource = true)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                if (string.IsNullOrWhiteSpace(baselinePath))
                {
                    return new
                    {
                        error = "compare mode needs baselinePath and currentPath: two heap snapshots written by " +
                                "profile_heap writeJson=true, taken before and after the workload.",
                        hint = "For a first look at a live process, pass processId (or processName) instead — it captures " +
                               "and saves the snapshot, and answers 'single-snapshot' without claiming growth.",
                    };
                }

                return await growth.CompareAsync(baselinePath, currentPath, tracePath, top, resolveSource);
            }

            var (target, error) = processes.Resolve(processId, processName);
            if (target is null) return new { error };

            var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
            return await growth.CaptureAsync(
                target.ProcessId, target.Name, baselinePath, tracePath, directory, top, resolveSource);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
