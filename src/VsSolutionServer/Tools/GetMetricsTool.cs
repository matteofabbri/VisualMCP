using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VsSolutionServer.Workspace;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class GetMetricsTool
{
    [McpServerTool, Description("Compute code metrics per method: cyclomatic complexity, lines of code, nesting depth, and parameter count. Requires LoadSolution first.")]
    public static async Task<object> GetMetrics(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Only return methods with cyclomatic complexity above this threshold (default: 1 = all methods)")] int minComplexity = 1)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var allMetrics = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                var model = await document.GetSemanticModelAsync();

                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var sym = model?.GetDeclaredSymbol(method);
                    if (sym is null) continue;

                    var cc        = CyclomaticComplexity(method);
                    var loc       = CountLogicalLines(method);
                    var depth     = MaxNestingDepth(method);
                    var paramCount = method switch
                    {
                        MethodDeclarationSyntax m          => m.ParameterList.Parameters.Count,
                        ConstructorDeclarationSyntax c     => c.ParameterList.Parameters.Count,
                        _                                  => 0,
                    };

                    if (cc < minComplexity) continue;

                    var lineSpan = method.GetLocation().GetLineSpan();
                    allMetrics.Add(new
                    {
                        Project             = project.Name,
                        Method              = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        FullName            = sym.ToDisplayString(),
                        FilePath            = document.FilePath,
                        Line                = lineSpan.StartLinePosition.Line + 1,
                        CyclomaticComplexity = cc,
                        LinesOfCode         = loc,
                        MaxNestingDepth     = depth,
                        ParameterCount      = paramCount,
                    });
                }
            }
        }

        allMetrics = allMetrics.OrderByDescending(m => ((dynamic)m).CyclomaticComplexity).ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            minComplexity,
            methodCount   = allMetrics.Count,
            metrics       = allMetrics,
        };
    }

    // CC = 1 + number of decision points
    private static int CyclomaticComplexity(SyntaxNode method)
    {
        int cc = 1;
        foreach (var node in method.DescendantNodes())
        {
            cc += node switch
            {
                IfStatementSyntax                                          => 1,
                ElseClauseSyntax                                           => 0, // already counted in if
                ForStatementSyntax                                         => 1,
                ForEachStatementSyntax                                     => 1,
                WhileStatementSyntax                                       => 1,
                DoStatementSyntax                                          => 1,
                CaseSwitchLabelSyntax                                      => 1,
                CasePatternSwitchLabelSyntax                               => 1,
                SwitchExpressionArmSyntax                                  => 1,
                CatchClauseSyntax                                          => 1,
                ConditionalExpressionSyntax                                => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression)  => 1,
                _                                                          => 0,
            };
        }
        return cc;
    }

    private static int CountLogicalLines(SyntaxNode method)
    {
        var span = method.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    private static int MaxNestingDepth(SyntaxNode method)
    {
        int maxDepth = 0;

        void Walk(SyntaxNode node, int depth)
        {
            if (depth > maxDepth) maxDepth = depth;
            foreach (var child in node.ChildNodes())
            {
                int next = child is BlockSyntax or SwitchSectionSyntax ? depth + 1 : depth;
                Walk(child, next);
            }
        }

        Walk(method, 0);
        return maxDepth;
    }
}
