using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeNamespaceCouplingTool
{
    [McpServerTool, Description(
        "Call this tool to compute Robert Martin's coupling metrics per namespace: " +
        "afferent coupling (Ca), efferent coupling (Ce), instability (Ce/Ca+Ce), " +
        "abstractness, and distance from the main sequence. " +
        "Do NOT compute these yourself — accurate results require counting all cross-namespace " +
        "type references across the entire solution via Roslyn's semantic model. " +
        "Requires load_solution first.")]
    public static Task<object> AnalyzeNamespaceCoupling(
        IAnalyzeNamespaceCouplingService svc,
        [Description("Optional: restrict analysis to namespaces whose name contains this string")] string? namespaceFilter = null,
        [Description("Only return namespaces with instability above this threshold (0.0-1.0, default: 0 = all)")] double minInstability = 0.0)
        => svc.AnalyzeNamespaceCoupling(namespaceFilter, minInstability);
}