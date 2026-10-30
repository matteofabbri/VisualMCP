using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileCallTreeTool
{
    [McpServerTool(Name = "profile_calltree"), Description(
        "Aggregate a trace's CPU samples into a call tree with inclusive and exclusive time — the layer above a flat " +
        "hotspot list. A method can be cheap itself and still own most of the time through a callee; TopInclusive " +
        "shows that, TopExclusive shows the methods that actually burn CPU, and the Path field gives the caller chain. " +
        "With writeFolded=true it also writes a folded-stacks file (FlameGraph / speedscope format) for a visual view. " +
        "Use it when profile_analyze's flat hotspot list does not explain where the time comes from.")]
    public static async Task<object> CallTree(
        ICallTreeService callTrees,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Path to the .nettrace file (profile='cpu' or 'runtime').")] string tracePath,
        [Description("How many nodes to return per list (1-500).")] int top = 40,
        [Description("Maximum node depth to include in the response (1-200).")] int maxDepth = 12,
        [Description("Resolve frames to file:line using portable PDBs (default true).")] bool resolveSource = true,
        [Description("Write a folded-stacks file (FlameGraph / speedscope) next to the trace or at outputPath.")] bool writeFolded = false,
        [Description("Optional path for the folded file (a directory ends with the trace name + .folded).")] string? outputPath = null)
    {
        try
        {
            if (!File.Exists(tracePath))
                return new { error = $"Trace not found: {tracePath}" };

            string? folded = null;
            if (writeFolded)
            {
                var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
                folded = string.IsNullOrWhiteSpace(outputPath)
                    ? Path.Combine(directory, Path.GetFileNameWithoutExtension(tracePath) + ".folded")
                    : outputPath;
            }

            var report = await callTrees.BuildAsync(tracePath, top, maxDepth, resolveSource, folded);
            return report;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
