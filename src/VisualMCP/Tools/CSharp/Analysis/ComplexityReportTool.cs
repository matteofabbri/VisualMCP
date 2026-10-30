using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class ComplexityReportTool
{
    [McpServerTool(Name = "complexity_report"), Description(
        "Drill into CYCLOMATIC and COGNITIVE complexity — how many paths a reader has to hold in their head, " +
        "not how the method scales with n (that is analyze_complexity). Pass filePath plus line or methodName " +
        "to get one method in full: the McCabe count, Sonar's cognitive score, nesting depth, Halstead volume, " +
        "maintainability index and EVERY decision point with its line, nesting level and cost — so you can see " +
        "whether 24 is twenty-four flat ifs or six ifs four levels deep, which are different problems. Each " +
        "report ends with the concrete refactorings that would lower it (extract a nested block, name a tangled " +
        "boolean, flatten a tail if into a guard clause) and whether reduce_complexity can apply them. Omit " +
        "filePath for the solution's worst methods ranked by cognitive complexity. Lambdas and local functions " +
        "are scored as their own units. Do NOT estimate complexity by reading code.")]
    public static Task<object> ComplexityReport(
        IComplexityReportService svc,
        [Description("Optional: file of the method to inspect. Omit to rank the whole solution.")] string? filePath = null,
        [Description("Optional: any line inside the method to inspect (1-based).")] int? line = null,
        [Description("Optional: method name, when you don't have a line.")] string? methodName = null,
        [Description("Optional: restrict the solution-wide ranking to one project.")] string? projectName = null,
        [Description("Solution-wide ranking: how many methods to return (default: 20).")] int topN = 20,
        [Description("Solution-wide ranking: minimum cyclomatic OR cognitive complexity to include (default: 5).")] int minComplexity = 5,
        [Description("Include the ranked decision points (default: true).")] bool includeContributors = true)
        => svc.ComplexityReport(filePath, line, methodName, projectName, topN, minComplexity, includeContributors);
}
