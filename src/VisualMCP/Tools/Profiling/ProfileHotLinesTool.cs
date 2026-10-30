using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileHotLinesTool
{
    [McpServerTool(Name = "profile_hotlines"), Description(
        "Answer 'which LINE inside this method burns the CPU?' from a .nettrace — the question the method-level " +
        "hotspot list cannot answer. Each CPU sample's instruction pointer is resolved through the method's IL→native " +
        "map (the JIT keyword, already in the cpu/diagnose presets) to an IL offset, then through the portable PDB to " +
        "the sequence point that covers it. Returns the self CPU per source line, ranked, with the code text of each " +
        "line; pass method= to scope the answer to one method (substring, case-insensitive) — shares are then of that " +
        "method's CPU, so a line that owns half of a small method still reads 50%. Samples that cannot be placed (no " +
        "PDB, no IL→native map, framework code) are reported as unattributed instead of being dumped on line 1. " +
        "Use it after profile_analyze/profile_hotpaths names the method, before changing a statement.")]
    public static async Task<object> HotLines(
        ILineCostService lineCosts,
        [Description("Path to the .nettrace file.")] string tracePath,
        [Description("Optional method filter (substring of the full frame name, case-insensitive), e.g. 'ProcessOrder'.")] string? method = null,
        [Description("How many lines to return (1-100).")] int top = 20,
        [Description("Minimum share (percent of the scope's CPU) for a line to be reported (0-100).")] double minPercent = 0.5,
        [Description("Resolve frames to file:line using portable PDBs (default true; without it there is no line to attribute).")] bool resolveSource = true,
        [Description("Include the source text of each reported line (default true).")] bool includeText = true)
    {
        try
        {
            return await lineCosts.AnalyzeAsync(
                tracePath, method, top, minPercent, resolveSource, includeText);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
