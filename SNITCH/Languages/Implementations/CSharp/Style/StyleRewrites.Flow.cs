using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Analysis;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0059: an assignment whose value is overwritten before anything reads it. The verdict comes from
/// Roslyn's data-flow analysis (<see cref="FlowFacts"/>), not from matching text — a statement between
/// the two stores that mentions the variable is not necessarily a read, and one that does not mention it
/// can still read it through a closure, which is why the answer is computed.
/// </summary>
public sealed class UnnecessaryAssignmentRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0059", "Remove unnecessary value assignment",
        "x = a; x = b; → x = b;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ExpressionStatementSyntax statement) return null;
        if (statement.Expression is not AssignmentExpressionSyntax assignment) return null;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return null;
        if (assignment.Left is not IdentifierNameSyntax target) return null;

        // A right side with a side effect must not disappear, and one that reads the variable makes the
        // statement a real update rather than a dead store.
        if (!IsSideEffectFree(assignment.Right)) return null;

        if (model.GetSymbolInfo(target, cancellationToken).Symbol is not ILocalSymbol local) return null;
        if (FlowFacts.Reads(statement, local, model, cancellationToken)) return null;
        if (!FlowFacts.IsOverwrittenBeforeRead(statement, local, model, cancellationToken)) return null;

        return Removal(statement);
    }
}
