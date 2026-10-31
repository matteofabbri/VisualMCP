using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.Style;

/// <summary>What a reference does to the symbol it names.</summary>
internal enum VbSymbolUsage
{
    Read,
    Write,
    ReadWrite,
    Other,
}

/// <summary>One place a symbol is used, with the file and the node the use sits on.</summary>
internal sealed record VbSymbolReference(
    string FilePath,
    int Line,
    SyntaxNode Node,
    VbSymbolUsage Usage,
    bool InConstructor);

/// <summary>
/// The questions a VB style rule asks about a symbol or a statement: where a member is used and how,
/// and what a statement reads and writes. This is the VB half of the C# <c>SymbolFacts</c>/<c>FlowFacts</c>:
/// Roslyn's renamer and data-flow analysis do the work, so the answers are semantic rather than textual.
/// </summary>
internal static class VbStyleFacts
{
    /// <summary>Every reference of <paramref name="symbol"/> in the solution, with what it does.</summary>
    public static IReadOnlyList<VbSymbolReference> References(
        ISymbol symbol, Solution solution, CancellationToken cancellationToken = default)
    {
        var found = SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).GetAwaiter().GetResult();
        var references = new List<VbSymbolReference>();

        foreach (var reference in found)
        {
            foreach (var location in reference.Locations)
            {
                if (location.IsImplicit) continue;

                var root = location.Document.GetSyntaxRootAsync(cancellationToken).GetAwaiter().GetResult();
                if (root is null) continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null) continue;

                var target = WrittenTarget(node);

                references.Add(new VbSymbolReference(
                    location.Document.FilePath ?? string.Empty,
                    location.Location.GetLineSpan().StartLinePosition.Line + 1,
                    target,
                    Classify(target),
                    target.AncestorsAndSelf().OfType<ConstructorBlockSyntax>().Any()));
            }
        }

        return references;
    }

    /// <summary>
    /// Whether the value a statement stores into <paramref name="symbol"/> is overwritten before anything
    /// reads it. Conservative in the same way the C# rule is: a nested lambda declared in between could
    /// run later and read it, so the answer is "no" there.
    /// </summary>
    public static bool IsOverwrittenBeforeRead(
        StatementSyntax statement, ILocalSymbol symbol, SemanticModel model, CancellationToken cancellationToken)
    {
        if (statement.Parent is null) return false;

        var siblings = VbBlocks.StatementsOf(statement.Parent);
        var index = IndexOf(siblings, statement);
        if (index < 0) return false;

        for (var next = index + 1; next < siblings.Count; next++)
        {
            var following = (StatementSyntax)siblings[next];

            if (following.DescendantNodes().Any(node => node is LambdaExpressionSyntax))
                return false;

            var analysis = model.AnalyzeDataFlow(following);
            if (analysis is not { Succeeded: true }) return false;

            if (analysis.ReadInside.Contains(symbol, SymbolEqualityComparer.Default)) return false;
            if (analysis.WrittenInside.Contains(symbol, SymbolEqualityComparer.Default)) return true;
        }

        return false;
    }

    /// <summary>The position of a node in a statement list, or -1 when it is not one of them.</summary>
    public static int IndexOf(IReadOnlyList<SyntaxNode> statements, SyntaxNode node)
    {
        for (var index = 0; index < statements.Count; index++)
            if (statements[index] == node)
                return index;

        return -1;
    }

    /// <summary>Whether the statement itself reads the symbol, which makes it a read-modify-write.</summary>
    public static bool Reads(
        StatementSyntax statement, ILocalSymbol symbol, SemanticModel model, CancellationToken cancellationToken) =>
        model.AnalyzeDataFlow(statement) is { Succeeded: true } analysis &&
        analysis.ReadInside.Contains(symbol, SymbolEqualityComparer.Default);

    /// <summary>True when the expression can be dropped or written twice without running anything twice.</summary>
    public static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MeExpressionSyntax => true,
        GetTypeExpressionSyntax => true,
        ParenthesizedExpressionSyntax parenthesized => IsSideEffectFree(parenthesized.Expression),
        MemberAccessExpressionSyntax member => member.Expression is null || IsSideEffectFree(member.Expression),
        _ => expression.IsKind(SyntaxKind.NameOfExpression),
    };

    /// <summary>The node that actually stores a value: the left side of an assignment, else the node itself.</summary>
    private static SyntaxNode WrittenTarget(SyntaxNode node)
    {
        var assignment = node.AncestorsAndSelf().OfType<AssignmentStatementSyntax>().FirstOrDefault();
        if (assignment is not null)
            return assignment.Left.Span.Contains(node.Span) ? assignment.Left : node;

        return node;
    }

    private static VbSymbolUsage Classify(SyntaxNode target)
    {
        var assignment = target.AncestorsAndSelf().OfType<AssignmentStatementSyntax>().FirstOrDefault();
        if (assignment is null) return VbSymbolUsage.Read;

        if (!assignment.Left.Span.Contains(target.Span)) return VbSymbolUsage.Read;

        return assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)
            ? VbSymbolUsage.Write
            : VbSymbolUsage.ReadWrite;
    }
}
