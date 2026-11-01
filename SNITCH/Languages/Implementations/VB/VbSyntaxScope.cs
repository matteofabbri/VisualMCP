using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB;

/// <summary>
/// The nodes of a Visual Basic unit that belong to that unit: everything below it except the inside of
/// a nested <c>Sub</c>/<c>Function</c> or a lambda, whose decisions are scored on their own.
/// <para>
/// The filter is an ancestor test on a plain <see cref="SyntaxNode.DescendantNodes()"/> walk rather than
/// the predicate overload. That overload returns only the nodes its predicate accepts — a node it
/// rejects is not in the result at all — so a predicate written as "not a lambda and not a method"
/// silently drops the very blocks that should be found.
/// </para>
/// </summary>
public static class VbSyntaxScope
{
    /// <summary>Every descendant of <paramref name="unit"/> that is not inside a nested unit.</summary>
    public static IEnumerable<SyntaxNode> OwnNodes(SyntaxNode unit) =>
        unit.DescendantNodes().Where(node => !IsInsideNestedUnit(node, unit));

    /// <summary>The node itself and its own descendants, without the inside of a nested unit.</summary>
    public static IEnumerable<SyntaxNode> OwnNodesAndSelf(SyntaxNode unit) =>
        unit.DescendantNodesAndSelf().Where(node => !IsInsideNestedUnit(node, unit));

    private static bool IsInsideNestedUnit(SyntaxNode node, SyntaxNode unit)
    {
        // The scope's own root is never "inside" itself: its ancestor chain runs through the method
        // block that contains it, which is not nested relative to this walk.
        if (node == unit) return false;

        return node.Ancestors()
            .TakeWhile(ancestor => ancestor != unit)
            .Any(ancestor => ancestor is LambdaExpressionSyntax or MethodBlockSyntax);
    }
}
