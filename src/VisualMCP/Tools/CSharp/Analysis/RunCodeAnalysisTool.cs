using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class RunCodeAnalysisTool
{
    [McpServerTool(Name = "run_code_analysis"), Description(
        "Run the project's configured Roslyn code analyzers (the .NET analyzers, StyleCop, Roslynator, etc. " +
        "referenced by the project) and return their diagnostics with id, severity, category, message and " +
        "location. This is the analyzer/code-style pass — complementary to get_diagnostics (compiler) and " +
        "build_project (MSBuild). Use it to surface analyzer warnings the compiler alone does not report. " +
        "The solution auto-loads on first use.")]
    public static Task<object> RunCodeAnalysis(
        [Description("Optional: restrict to a single project by name.")] string? projectName = null,
        [Description("Minimum severity to include: Error, Warning, Info, Hidden (default: Warning).")] string minSeverity = "Warning",
        [Description("Maximum number of diagnostics to return (default: 500).")] int maxResults = 500,
        [Description("Timeout in seconds for the analysis (default: 180, max: 600).")] int timeoutSeconds = 180)
        => RunCodeAnalysisImpl.RunAsync(projectName, minSeverity, maxResults, timeoutSeconds);
}
