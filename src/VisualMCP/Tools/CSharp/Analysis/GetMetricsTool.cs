using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class GetMetricsTool
{
    [McpServerTool, Description(
        "Call this tool to compute per-method code metrics: cyclomatic complexity, lines of code, " +
        "nesting depth, and parameter count. " +
        "Do NOT compute cyclomatic complexity yourself — it requires counting all decision paths " +
        "in the control-flow graph, which Roslyn derives precisely from the AST. " +
        "For a solution-wide ranking of the most complex methods, use get_complexity_hotspots instead. " +
        "Requires load_solution first.")]
    public static Task<object> GetMetrics(
        IGetMetricsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Only return methods with cyclomatic complexity above this threshold (default: 1 = all methods)")] int minComplexity = 1)
        => svc.GetMetrics(projectName, minComplexity);
}