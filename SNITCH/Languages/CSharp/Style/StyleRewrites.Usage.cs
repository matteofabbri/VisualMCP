using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0080: the null-forgiving operator (<c>x!</c>) on a value the flow analysis already knows is not
/// null. This is the first rule that reads Roslyn's nullability data instead of guessing from syntax.
/// </summary>
public sealed class UnnecessarySuppressionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0080", "Remove unnecessary suppression operator",
        "value! → value when the value cannot be null");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not PostfixUnaryExpressionSyntax suppression) return null;
        if (!suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression)) return null;

        // The operand must already be non-null on this path; a maybe-null value keeps its `!`.
        if (model.GetTypeInfo(suppression.Operand, cancellationToken).Nullability.FlowState
            != NullableFlowState.NotNull) return null;
        if (suppression.Parent is MemberAccessExpressionSyntax access && access.Expression == suppression)
        {
            // `x!.M` becomes `x.M`, which is the point; nothing extra to check.
        }

        return Match(node, suppression.Operand.WithTriviaFrom(suppression));
    }
}

/// <summary>
/// IDE0058: a statement whose value nobody reads says so explicitly — <c>M();</c> becomes
/// <c>_ = M();</c>. Only the shapes that can legally stand as a statement are considered, and an
/// assignment is never touched: it has no value to discard.
/// </summary>
public sealed class UnusedExpressionValueRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0058", "Expression value is never used", "_ = M(); where M returns a value");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ExpressionStatementSyntax statement) return null;

        switch (statement.Expression)
        {
            case InvocationExpressionSyntax:
            case ObjectCreationExpressionSyntax:
            case ConditionalAccessExpressionSyntax:
                break;

            // An assignment already states what it does with the value.
            default:
                return null;
        }

        var type = model.GetTypeInfo(statement.Expression, cancellationToken).Type;
        if (type is null || type.SpecialType == SpecialType.System_Void) return null;

        return MatchStatement(node, $"_ = {statement.Expression};");
    }
}
