using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0045 / IDE0046: an if/else whose two branches do the same thing to different values is one
/// conditional expression. Both branches must be exactly one statement with no comment in them —
/// the whole if disappears, and a comment inside it would disappear with it.
/// </summary>
internal static class ConditionalShape
{
    public static StatementSyntax? Single(StatementSyntax statement) =>
        statement is BlockSyntax block
            ? block.Statements.Count == 1 ? block.Statements[0] : null
            : statement;

    public static bool HasComment(SyntaxNode node) =>
        node.DescendantTrivia(descendIntoTrivia: true).Any(trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
}

/// <summary>IDE0046: <c>if (c) return a; else return b;</c> → <c>return c ? a : b;</c>.</summary>
public sealed class ConditionalReturnRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0046", "Use conditional expression for return",
        "if (c) return a; else return b; → return c ? a : b;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not IfStatementSyntax ifStatement || ifStatement.Else is null) return null;
        if (ConditionalShape.HasComment(ifStatement)) return null;

        if (ConditionalShape.Single(ifStatement.Statement) is not ReturnStatementSyntax first) return null;
        if (first.Expression is not { } whenTrue) return null;

        if (ConditionalShape.Single(ifStatement.Else.Statement) is not ReturnStatementSyntax second) return null;
        if (second.Expression is not { } whenFalse) return null;

        // The conditional infers one type for both arms: two different types would silently convert
        // one of them, which is not the code that was there.
        var left = model.GetTypeInfo(whenTrue, cancellationToken).Type;
        var right = model.GetTypeInfo(whenFalse, cancellationToken).Type;
        if (left is not null && right is not null && !SameSymbol(left, right)) return null;

        return MatchStatement(node, $"return {ifStatement.Condition} ? {whenTrue} : {whenFalse};");
    }
}

/// <summary>IDE0045: <c>if (c) x = a; else x = b;</c> → <c>x = c ? a : b;</c>.</summary>
public sealed class ConditionalAssignmentRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0045", "Use conditional expression for assignment",
        "if (c) x = a; else x = b; → x = c ? a : b;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not IfStatementSyntax ifStatement || ifStatement.Else is null) return null;
        if (ConditionalShape.HasComment(ifStatement)) return null;

        if (ConditionalShape.Single(ifStatement.Statement) is not ExpressionStatementSyntax first) return null;
        if (first.Expression is not AssignmentExpressionSyntax whenTrue) return null;

        if (ConditionalShape.Single(ifStatement.Else.Statement) is not ExpressionStatementSyntax second) return null;
        if (second.Expression is not AssignmentExpressionSyntax whenFalse) return null;

        if (!whenTrue.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
            !whenFalse.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            return null;
        }

        // Both branches must write the same thing: the same symbol, not merely the same text.
        var target = model.GetSymbolInfo(whenTrue.Left, cancellationToken).Symbol;
        if (target is null) return null;
        if (!SameSymbol(model.GetSymbolInfo(whenFalse.Left, cancellationToken).Symbol, target)) return null;

        // Same reason as the return form: one inferred type must fit both arms.
        var left = model.GetTypeInfo(whenTrue.Right, cancellationToken).Type;
        var right = model.GetTypeInfo(whenFalse.Right, cancellationToken).Type;
        if (left is not null && right is not null && !SameSymbol(left, right)) return null;

        return MatchStatement(node,
            $"{whenTrue.Left} = {ifStatement.Condition} ? {whenTrue.Right} : {whenFalse.Right};");
    }
}
