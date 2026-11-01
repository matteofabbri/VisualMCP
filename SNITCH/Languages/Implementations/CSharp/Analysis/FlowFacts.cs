using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The flow facts Roslyn already computes, asked one question at a time. A rule should not re-implement
/// "is this value ever read": <see cref="SemanticModel.AnalyzeDataFlow(StatementSyntax)"/> knows, and
/// this is the one place that asks it.
/// </summary>
public static class FlowFacts
{
    /// <summary>
    /// Whether the value <paramref name="statement"/> stores into <paramref name="symbol"/> is overwritten
    /// before anything reads it. Conservative by construction: a later statement that contains a nested
    /// function could read the variable when it is called, so the answer is "no" there.
    /// </summary>
    public static bool IsOverwrittenBeforeRead(
        StatementSyntax statement, ILocalSymbol symbol, SemanticModel model, CancellationToken cancellationToken)
    {
        if (statement.Parent is not BlockSyntax block) return false;

        var index = block.Statements.IndexOf(statement);
        if (index < 0) return false;

        for (var next = index + 1; next < block.Statements.Count; next++)
        {
            var following = block.Statements[next];

            // A closure declared here may run later and read the variable.
            if (following.DescendantNodes().Any(node =>
                    node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                return false;
            }

            var analysis = model.AnalyzeDataFlow(following);
            if (analysis is not { Succeeded: true }) return false;

            if (analysis.ReadInside.Contains(symbol, SymbolEqualityComparer.Default)) return false;
            if (analysis.WrittenInside.Contains(symbol, SymbolEqualityComparer.Default)) return true;
        }

        // No later write in this block: the value may still be read after it, so it is not dead.
        return false;
    }

    /// <summary>Whether the statement itself reads the symbol, which makes it a read-modify-write.</summary>
    public static bool Reads(StatementSyntax statement, ILocalSymbol symbol, SemanticModel model, CancellationToken cancellationToken) =>
        model.AnalyzeDataFlow(statement) is { Succeeded: true } analysis &&
        analysis.ReadInside.Contains(symbol, SymbolEqualityComparer.Default);
}
