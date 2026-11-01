using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Analysis;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0052: a private field that is written and never read holds nothing. Removing it means removing
/// every assignment that writes it too, or the file stops compiling — so this is a span-edit suggestion
/// over the declaration and every store, and the assignments are only touched while their right side is
/// safe to drop.
/// </summary>
public sealed class UnreadPrivateMemberRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0052", "Remove unread private member",
        "private int _x; _x = 1; → both lines removed when nothing reads _x");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null || !context.IsPristine) return null;
        if (node is not FieldDeclarationSyntax field) return null;
        if (field.Declaration.Variables.Count != 1) return null;
        if (field.AttributeLists.Count > 0) return null;
        if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword))
            return null;

        if (context.Model.GetDeclaredSymbol(field.Declaration.Variables[0], context.CancellationToken)
            is not IFieldSymbol symbol) return null;
        if (symbol.DeclaredAccessibility != Accessibility.Private) return null;

        var references = SymbolFacts.References(symbol, context.Solution, context.CancellationToken);
        if (references.Count == 0) return null;                       // nothing writes it: that is IDE0051
        if (references.Any(reference => reference.Usage != SymbolUsage.Write)) return null;

        var edits = new List<SpanEdit>();

        var declarationPath = field.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(declarationPath)) return null;
        edits.Add(new SpanEdit(declarationPath, field.Span.Start, field.Span.Length));

        foreach (var reference in references)
        {
            if (string.IsNullOrEmpty(reference.FilePath)) return null;

            // Only a store that stands alone: `_x = 1;`. Anything folded into another expression keeps
            // its shape, and a right side with a side effect must not disappear with it.
            if (reference.Node.Parent is not AssignmentExpressionSyntax assignment) return null;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return null;
            if (assignment.Parent is not ExpressionStatementSyntax statement) return null;
            if (!IsSideEffectFree(assignment.Right)) return null;

            edits.Add(new SpanEdit(reference.FilePath, statement.Span.Start, statement.Span.Length));
        }

        return Shape(Rule, field, edits, symbol.Name, string.Empty);
    }
}
