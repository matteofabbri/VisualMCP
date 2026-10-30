using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileHotPathsTool
{
    [McpServerTool(Name = "profile_hotpaths"), Description(
        "Find hot paths like Visual Studio's Hot Path view — but return several at once instead of one per " +
        "recompile. From a .nettrace it produces: HotMethods (the fix list: one entry per user method/line, with the " +
        "cost summed across every stack through it, ranked by share), TopPaths (each hot root-to-leaf path with the " +
        "deepest user frame to open), and Branches (frames where the cost splits between several hot children, i.e. " +
        "where the choice is 'optimise the caller' rather than 'optimise one leaf'). Stack variants that reach the " +
        "same method are merged and counted, and the recommendation says whether the cost is self time or is spread " +
        "through callees/framework code. Metrics: 'cpu' (CPU ms), 'allocations' (sampled bytes), 'exceptions' " +
        "(throw paths), 'mixed' (CPU and allocation lists side by side). Use it INSTEAD OF reading a call tree by hand " +
        "or iterating fix → recompile → re-measure one hotspot at a time.")]
    public static async Task<object> HotPaths(
        IHotPathService hotPaths,
        IRoslynWorkspace workspace,
        [Description("Path to the .nettrace file.")] string tracePath,
        [Description("'cpu' (default), 'allocations', 'exceptions' or 'mixed'.")] string metric = "cpu",
        [Description("How many methods / paths / branches to return (1-100).")] int top = 10,
        [Description("Minimum share (percent of the metric) for a path or branch to be reported.")] double minPercent = 1.0,
        [Description("Resolve frames to file:line using portable PDBs (default true).")] bool resolveSource = true,
        [Description("Only report user-code methods as actionable (source under the solution); false also allows framework frames with a PDB.")] bool userCodeOnly = true)
    {
        try
        {
            if (!File.Exists(tracePath))
                return new { error = $"Trace not found: {tracePath}" };

            var solutionDirectory = ProfilingToolHelpers.SolutionDirectory(workspace);
            var report = await hotPaths.AnalyzeAsync(
                tracePath, metric, top, minPercent, resolveSource, userCodeOnly, solutionDirectory);

            return report;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
