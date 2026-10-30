using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalyzeNullabilityTool
{
    [McpServerTool(Name = "analyze_nullability"), Description(
        "Where nullable reference types actually protect the code: whether the context is enabled per " +
        "project, where the `!` suppressions are (with their lines), and which CS86xx warnings the flow " +
        "analysis raised. A project with nullable disabled gets no diagnostics at all, and a file full " +
        "of `!` has the same effect one expression at a time — this reports both.")]
    public static Task<object> AnalyzeNullability(
        IAnalyzeNullabilityService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("How many files to return, worst first (default 20)")] int top = 20)
        => svc.Analyze(projectName, top);
}
