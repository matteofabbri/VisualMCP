using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>What a reference does to the symbol it names.</summary>
public enum SymbolUsage
{
    Read,
    Write,
    ReadWrite,
}

/// <summary>One reference of a symbol, with what it does and the node it sits on.</summary>
public sealed record SymbolReference(
    string FilePath, int Line, SyntaxNode Node, SymbolUsage Usage, bool InConstructor);

/// <summary>
/// Where a symbol is used, and whether each use reads it, writes it, or both. This is the analysis
/// several rules need and none should re-implement: "written but never read" (IDE0052), "this assignment
/// is dead" (IDE0059), "only written in the constructor" (IDE0044). It is C#-specific because "what is a
/// write" is syntax — Visual Basic will provide its own classifier over the same shape.
/// </summary>
public static class SymbolFacts
{
    public static IReadOnlyList<SymbolReference> References(
        ISymbol symbol, Solution solution, CancellationToken cancellationToken = default)
    {
        var found = SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).GetAwaiter().GetResult();

        var references = new List<SymbolReference>();

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
                var statement = target.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
                var member = statement?.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();

                references.Add(new SymbolReference(
                    location.Document.FilePath ?? string.Empty,
                    location.Location.GetLineSpan().StartLinePosition.Line + 1,
                    target,
                    Classify(target),
                    member is ConstructorDeclarationSyntax));
            }
        }

        return references;
    }

    /// <summary>
    /// The expression an assignment writes: for <c>this._x</c> or <c>a._x</c> the reference points at the
    /// name, while the thing being written is the whole member access.
    /// </summary>
    public static SyntaxNode WrittenTarget(SyntaxNode node)
    {
        while (node.Parent is MemberAccessExpressionSyntax member &&
               (member.Name.Span == node.Span || member.Expression.Span == node.Span))
        {
            node = member;
        }

        return node;
    }

    /// <summary>Read, write, or both — from the reference's own parent, never from its text.</summary>
    public static SymbolUsage Classify(SyntaxNode target) => target.Parent switch
    {
        AssignmentExpressionSyntax assignment when assignment.Left == target =>
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? SymbolUsage.Write : SymbolUsage.ReadWrite,

        PrefixUnaryExpressionSyntax prefix when prefix.Operand == target =>
            prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                ? SymbolUsage.ReadWrite
                : SymbolUsage.Read,

        PostfixUnaryExpressionSyntax postfix when postfix.Operand == target =>
            postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)
                ? SymbolUsage.ReadWrite
                : SymbolUsage.Read,

        ArgumentSyntax { RefKindKeyword: var keyword } when
            keyword.IsKind(SyntaxKind.RefKeyword) || keyword.IsKind(SyntaxKind.OutKeyword) => SymbolUsage.ReadWrite,

        _ => SymbolUsage.Read,
    };

    /// <summary>Every reference writes and none reads: the value is stored and never looked at.</summary>
    public static bool IsWrittenButNeverRead(
        ISymbol symbol, Solution solution, CancellationToken cancellationToken = default)
    {
        var references = References(symbol, solution, cancellationToken);
        return references.Count > 0 && references.All(reference => reference.Usage == SymbolUsage.Write);
    }
}
