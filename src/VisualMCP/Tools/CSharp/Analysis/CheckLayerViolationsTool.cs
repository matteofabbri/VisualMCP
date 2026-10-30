using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class CheckLayerViolationsTool
{
    [McpServerTool(Name = "check_layer_violations"), Description(
        "Layer enforcement over the real type usages: name the layers top-most first (e.g. " +
        "'Web,Application,Domain,Infrastructure') and every dependency that points back up is reported " +
        "with a file and line. A layer is matched by its namespace segment, so `App.Web` belongs to " +
        "'Web'. This is the architecture rule analyze_dependencies cannot express: that tool shows the " +
        "graph, this one says which edge is forbidden.")]
    public static Task<object> CheckLayerViolations(
        ICheckLayerViolationsService svc,
        [Description("Layer names in dependency order, top-most first, comma-separated")] string layers,
        [Description("Optional: only consider namespaces whose name contains this text")] string? namespaceFilter = null,
        [Description("Maximum violations to return (default 200)")] int maxResults = 200)
        => svc.Check(layers, namespaceFilter, maxResults);
}
