using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class ExportDependencyGraphTool
{
    [McpServerTool(Name = "export_dependency_graph"), Description(
        "The dependency graph as a diagram, not a JSON dump: Graphviz DOT or Mermaid, at project or " +
        "namespace level. Use analyze_dependencies to reason about cycles and roots; use this when the " +
        "graph has to end up in a document, a PR or a wiki page.")]
    public static Task<object> ExportDependencyGraph(
        IExportDependencyGraphService svc,
        [Description("dot | mermaid (default dot)")] string format = "dot",
        [Description("projects | namespaces (default projects)")] string level = "projects")
        => svc.Export(format, level);
}
