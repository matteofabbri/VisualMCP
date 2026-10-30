using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeDependenciesTool
{
    [McpServerTool, Description(
        "Call this tool to build the project-level dependency graph for the loaded solution, " +
        "detect circular project references, and identify root projects (nothing depends on them). " +
        "Do NOT trace project dependencies yourself by reading .csproj files — this tool uses " +
        "Roslyn's project reference model which is authoritative. " +
        "For namespace-level cycles within a project, use analyze_circular_namespace_dependencies instead. " +
        "Requires load_solution first.")]
    public static Task<object> AnalyzeDependencies(IAnalyzeDependenciesService svc)
        => svc.AnalyzeDependencies();
}