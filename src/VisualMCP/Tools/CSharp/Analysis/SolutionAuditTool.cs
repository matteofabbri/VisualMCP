using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class SolutionAuditTool
{
    [McpServerTool(Name = "solution_audit"), Description(
        "One-call health report for the WHOLE solution (or one project): analyzer-pack findings grouped by inspection id, " +
        "compiler errors/warnings grouped by id, the most complex methods, the largest files, the project reference graph " +
        "with cycles, and a short list of suggested first moves. Use this instead of calling inspect_code, get_diagnostics, " +
        "get_complexity_hotspots and analyze_dependencies one by one when you need the overall picture (code review, " +
        "onboarding, 'where do I start'). Read-only. For a deep dive on one finding use the dedicated tool afterwards.")]
    public static Task<object> SolutionAudit(
        ISolutionAuditService audit,
        [Description("Optional: restrict to one project by name.")] string? project = null,
        [Description("Rows per section (complexity, largest files, top ids). Default 10, max 50.")] int maxPerCategory = 10,
        [Description("Run the analyzer packs too (roslynator, netanalyzers, asyncfixer, meziantou). Default true.")] bool includePacks = true,
        [Description("Minimum inspection severity: ERROR, WARNING, WEAK_WARNING, TYPO or ALL (default WARNING).")] string minSeverity = "WARNING")
        => audit.AuditAsync(project, maxPerCategory, includePacks, minSeverity);
}
