using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class GetComplexityHotspotsTool
{
    [McpServerTool, Description(
        "Call this tool at the start of any refactoring session to get the top-N most complex " +
        "methods in the entire solution, ranked by cyclomatic complexity. " +
        "Each result includes file path, line number, and a risk label (Low/Medium/High/Critical). " +
        "Do NOT estimate complexity yourself by reading code — cyclomatic complexity requires " +
        "counting all decision paths in the control flow graph, which Roslyn computes precisely. " +
        "Requires load_solution first.")]
    public static Task<object> GetComplexityHotspots(
        IGetComplexityHotspotsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Number of top hotspots to return (default: 20)")] int topN = 20,
        [Description("Minimum cyclomatic complexity to include (default: 5)")] int minComplexity = 5)
        => svc.GetComplexityHotspots(projectName, topN, minComplexity);
}