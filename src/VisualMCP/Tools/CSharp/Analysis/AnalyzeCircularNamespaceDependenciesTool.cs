using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeCircularNamespaceDependenciesTool
{
    [McpServerTool, Description(
        "Call this tool to detect circular dependencies between namespaces within the solution. " +
        "Unlike analyze_dependencies (which works at the project reference level), this tool " +
        "analyses actual type usage inside source files to find namespace-level cycles " +
        "(e.g. Namespace A uses types from Namespace B, and B uses types from A). " +
        "Do NOT attempt to trace namespace cycles yourself by reading using directives — " +
        "only Roslyn's semantic model can resolve which types are actually used vs merely imported. " +
        "Requires load_solution first.")]
    public static Task<object> AnalyzeCircularNamespaceDependencies(
        IAnalyzeCircularNamespaceDependenciesService svc,
        [Description("Optional: restrict analysis to namespaces whose name contains this string")] string? namespaceFilter = null)
        => svc.AnalyzeCircularNamespaceDependencies(namespaceFilter);
}
