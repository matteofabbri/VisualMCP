using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeComplexityTool
{
    [McpServerTool, Description(
        "Estimates the COMPUTATIONAL (time) and SPATIAL (memory) complexity of the solution's callables " +
        "as Big-O, with evidence and fix suggestions. Call this instead of reading methods and guessing: " +
        "it counts loop nesting and bounds, LINQ chains, sorting, linear collection lookups inside loops, " +
        "recursion patterns (subtractive, halving, branching) and allocation/materialisation sites, and " +
        "propagates the complexity of in-solution callees. Returns per method: TimeComplexity, SpaceComplexity, " +
        "Confidence, the loops with their line and iteration class, the recursion shape, memory sites, " +
        "evidence and recommendations, plus a distribution over the whole solution. " +
        "This is a static upper-bound heuristic — use profile_collect/profile_analyze when measured cost matters. " +
        "Requires load_solution first.")]
    public static Task<object> AnalyzeComplexity(
        IAnalyzeComplexityService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to files whose path contains this text (absolute path or a fragment like 'Services/')")] string? filePath = null,
        [Description("Optional: restrict to methods whose name or full name contains this text")] string? methodName = null,
        [Description("Maximum number of methods to return, worst first (default: 25, max: 200)")] int topN = 25,
        [Description("Only return methods at least this complex: O(1), O(log n), O(n), O(n log n), O(n^2), O(n^3), O(2^n), O(?) — default O(n)")] string minComplexity = "O(n)")
        => svc.AnalyzeComplexity(projectName, filePath, methodName, topN, minComplexity);
}
