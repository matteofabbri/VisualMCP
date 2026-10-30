using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileAllocationsTool
{
    [McpServerTool(Name = "profile_allocations"), Description(
        "Attribute sampled allocation bytes to the method that performed the allocation — the answer to 'where is " +
        "this allocation coming from', which a type-level list cannot give. Returns, per allocation site: the leaf " +
        "method, file:line when the PDB is available, the allocated type, sampled MB, sample count, share and the " +
        "kind (small/large object), plus the top full allocation stacks and rule-based findings (dominant site, " +
        "dominant type, LOH share). It reads the same .nettrace as profile_analyze, but the trace must contain " +
        "allocation ticks with call stacks: collect with profile='gc' (Verbose + stackwalk), which profile_collect " +
        "now does by default. Use it INSTEAD OF guessing from allocation totals.")]
    public static async Task<object> Allocations(
        IAllocationSiteService allocations,
        [Description("Path to the .nettrace file collected with profile='gc' (or any trace with allocation stacks).")] string tracePath,
        [Description("How many sites / types / stacks to return (1-200).")] int top = 25,
        [Description("Only include allocations of types whose name contains this text (case-insensitive).")] string? focusType = null,
        [Description("Map the allocation site to file:line using portable PDBs (default true).")] bool resolveSource = true)
    {
        try
        {
            if (!File.Exists(tracePath))
                return new { error = $"Trace not found: {tracePath}" };

            var report = await allocations.AnalyzeAsync(tracePath, top, focusType, resolveSource);
            return report;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
