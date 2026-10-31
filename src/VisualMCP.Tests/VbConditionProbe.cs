using System.Text;
using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Languages.VB;
using Xunit;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VisualMCP.Tests;

/// <summary>Temporary probe for the VB condition planner path.</summary>
public sealed class VbConditionProbe
{
    private const string Source = """
        Friend Class Conditions
            Public Function Check(a As Boolean, b As Boolean, c As Boolean, d As Boolean) As Boolean
                If a AndAlso b AndAlso c AndAlso d Then
                    Return True
                End If
                Return False
            End Function
        End Class
        """;

    [Fact]
    public async Task Probe()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Conditions.vb", Source);
        var document = workspace.CurrentSolution!.Projects.First().Documents.First();
        var model = (await document.GetSemanticModelAsync())!;
        var root = (await document.GetSyntaxRootAsync())!;

        var report = new StringBuilder();
        var unit = ComplexityAnalyzer.MethodUnits(root).Single(node => ComplexityAnalyzer.NameOf(node) == "Check");
        var method = (VbSyntax.MethodBlockSyntax)unit;

        report.AppendLine($"ownNodes={VbSyntaxScope.OwnNodes(method).Count()}");
        var ifBlocks = VbSyntaxScope.OwnNodes(method).OfType<VbSyntax.MultiLineIfBlockSyntax>().ToList();
        report.AppendLine($"ifBlocks={ifBlocks.Count}");
        foreach (var ifBlock in ifBlocks)
        {
            var condition = ifBlock.IfStatement.Condition;
            report.AppendLine($"condition='{condition}' type={condition.GetType().Name}");
            report.AppendLine($"operators={VbSyntaxScope.OwnNodesAndSelf(condition).Count(n => n is VbSyntax.BinaryExpressionSyntax b && b.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.AndAlsoExpression))}");
        }

        var suggestions = ComplexityPlanner.Suggest(unit, model);
        report.AppendLine($"suggestions={suggestions.Count}");
        foreach (var suggestion in suggestions)
            report.AppendLine($"  {suggestion.Kind} auto={suggestion.AutoApplicable} blocked={suggestion.BlockedBy}");

        Directory.CreateDirectory(@"D:\REPOSITORY\vmcp-tmp");
        await File.WriteAllTextAsync(@"D:\REPOSITORY\vmcp-tmp\condition-probe.txt", report.ToString());
    }
}
