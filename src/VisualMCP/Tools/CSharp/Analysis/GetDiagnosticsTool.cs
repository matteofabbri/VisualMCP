using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool, Description(
        "Call this tool to get all compiler errors and warnings for the loaded solution — " +
        "equivalent to Visual Studio's Error List. " +
        "Do NOT infer compilation errors by reading source — this tool runs the full Roslyn " +
        "compilation pipeline and returns authoritative diagnostics with exact locations. " +
        "Requires load_solution first.")]
    public static Task<object> GetDiagnostics(
        IGetDiagnosticsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Minimum severity to include: Error, Warning, Info, Hidden (default: Warning)")] string minSeverity = "Warning")
        => svc.GetDiagnostics(projectName, minSeverity);
}