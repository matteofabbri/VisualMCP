using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

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
    public static async Task<object> GetComplexityHotspots(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Number of top hotspots to return (default: 20)")] int topN = 20,
        [Description("Minimum cyclomatic complexity to include (default: 5)")] int minComplexity = 5)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var results = new List<(int Cc, object Entry)>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var sym = model.GetDeclaredSymbol(method);
                    if (sym is null) continue;

                    var cc = CyclomaticComplexity(method);
                    if (cc < minComplexity) continue;

                    var loc        = method.GetLocation().GetLineSpan();
                    var lines      = loc.EndLinePosition.Line - loc.StartLinePosition.Line + 1;
                    var paramCount = method switch
                    {
                        MethodDeclarationSyntax m      => m.ParameterList.Parameters.Count,
                        ConstructorDeclarationSyntax c => c.ParameterList.Parameters.Count,
                        _                              => 0,
                    };

                    results.Add((cc, new
                    {
                        Rank                 = 0, // filled below
                        RiskLabel            = RiskLabel(cc),
                        CyclomaticComplexity = cc,
                        Method               = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        FullName             = sym.ToDisplayString(),
                        Project              = project.Name,
                        FilePath             = document.FilePath,
                        Line                 = loc.StartLinePosition.Line + 1,
                        LinesOfCode          = lines,
                        ParameterCount       = paramCount,
                    }));
                }
            }
        }

        var hotspots = results
            .OrderByDescending(r => r.Cc)
            .Take(topN)
            .Select((r, i) =>
            {
                dynamic e = r.Entry;
                return (object)new
                {
                    Rank                 = i + 1,
                    e.RiskLabel,
                    e.CyclomaticComplexity,
                    e.Method,
                    e.FullName,
                    e.Project,
                    e.FilePath,
                    e.Line,
                    e.LinesOfCode,
                    e.ParameterCount,
                };
            })
            .ToList();

        var distribution = new
        {
            Critical = results.Count(r => r.Cc >= 25),
            High     = results.Count(r => r.Cc is >= 15 and < 25),
            Medium   = results.Count(r => r.Cc is >= 10 and < 15),
            Low      = results.Count(r => r.Cc is >= 5  and < 10),
        };

        return new
        {
            projectFilter   = projectName ?? "all",
            topN,
            minComplexity,
            totalAboveThreshold = results.Count,
            distribution,
            hotspots,
        };
    }

    private static string RiskLabel(int cc) => cc switch
    {
        >= 25 => "Critical",
        >= 15 => "High",
        >= 10 => "Medium",
        _     => "Low",
    };

    private static int CyclomaticComplexity(SyntaxNode method)
    {
        int cc = 1;
        foreach (var node in method.DescendantNodes())
        {
            cc += node switch
            {
                IfStatementSyntax                                                           => 1,
                ForStatementSyntax                                                          => 1,
                ForEachStatementSyntax                                                      => 1,
                WhileStatementSyntax                                                        => 1,
                DoStatementSyntax                                                           => 1,
                CaseSwitchLabelSyntax                                                       => 1,
                CasePatternSwitchLabelSyntax                                                => 1,
                SwitchExpressionArmSyntax                                                   => 1,
                CatchClauseSyntax                                                           => 1,
                ConditionalExpressionSyntax                                                 => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression)    => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression)     => 1,
                _                                                                           => 0,
            };
        }
        return cc;
    }
}
