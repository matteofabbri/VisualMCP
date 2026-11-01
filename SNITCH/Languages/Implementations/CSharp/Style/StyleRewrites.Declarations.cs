using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0017: <c>var x = new C(); x.A = 1; x.B = 2;</c> → <c>var x = new C { A = 1, B = 2 };</c>.
/// The assignments must come straight after the declaration and must not read the variable: an
/// initializer evaluates its values before the object is assigned.
/// </summary>
public sealed class ObjectInitializerRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0017", "Object initialization can be simplified",
        "var x = new C(); x.A = 1; x.B = 2; → var x = new C { A = 1, B = 2 };");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (declaration.Modifiers.Any() || declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return null;

        var declarator = declaration.Declaration.Variables.SingleOrNone();
        if (declarator?.Initializer?.Value is not ObjectCreationExpressionSyntax creation) return null;
        if (creation.Initializer is { Expressions.Count: > 0 }) return null;

        var name = declarator.Identifier.ValueText;
        var initializer = creation.Initializer ?? InitializerExpression(SyntaxKind.ObjectInitializerExpression);

        var block = declaration.Parent as BlockSyntax;
        if (block is null) return null;

        var index = block.Statements.IndexOf(declaration);
        if (index < 0) return null;

        var assignments = new List<ExpressionSyntax>();
        StatementSyntax? last = null;

        for (var next = index + 1; next < block.Statements.Count; next++)
        {
            if (block.Statements[next] is not ExpressionStatementSyntax
                { Expression: AssignmentExpressionSyntax assignment } statement) break;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) break;
            if (assignment.Left is not MemberAccessExpressionSyntax member) break;
            if (member.Expression is not IdentifierNameSyntax target || target.Identifier.ValueText != name) break;

            // The value is evaluated before the object exists, so it cannot read the object itself.
            if (assignment.Right.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText == name)) return null;

            if (!IsSettable(member, model, cancellationToken)) break;

            assignments.Add(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                member.Name,
                assignment.Right.WithoutLeadingTrivia()));

            last = statement;
        }

        if (assignments.Count == 0 || last is null) return null;

        var rewritten = creation
            .WithArgumentList(creation.ArgumentList is { Arguments.Count: 0 } ? null : creation.ArgumentList)
            .WithInitializer(initializer.WithExpressions(SeparatedList(assignments)))
            .WithTrailingTrivia(creation.GetTrailingTrivia());

        var rewrittenDeclaration = declaration.WithDeclaration(
            declaration.Declaration.WithVariables(
                SeparatedList([declarator.WithInitializer(declarator.Initializer.WithValue(rewritten))])));

        return MatchShape(declaration, last, rewrittenDeclaration.ToString());
    }

    /// <summary>Whether the member can be written from an object initializer at all.</summary>
    private static bool IsSettable(MemberAccessExpressionSyntax member, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(member, cancellationToken).Symbol switch
        {
            IPropertySymbol property => property.SetMethod is not null,
            IFieldSymbol field => !field.IsReadOnly && !field.IsConst,
            _ => false,
        };
}

/// <summary>
/// IDE0042: <c>var t = Get(); var a = t.Item1; var b = t.Item2;</c> → <c>var (a, b) = Get();</c>.
/// The element locals must have the element's own type, or the deconstruction would widen them.
/// </summary>
public sealed class DeconstructionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0042", "Deconstruct variable declaration",
        "var t = Get(); var a = t.Item1; var b = t.Item2; → var (a, b) = Get();");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (declaration.Modifiers.Any() || declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return null;

        var declarator = declaration.Declaration.Variables.SingleOrNone();
        if (declarator?.Initializer is not { } source) return null;

        var name = declarator.Identifier.ValueText;
        if (model.GetDeclaredSymbol(declarator, cancellationToken) is not ILocalSymbol local) return null;
        if (local.Type is not INamedTypeSymbol { IsTupleType: true } tuple) return null;

        var block = declaration.Parent as BlockSyntax;
        if (block is null) return null;

        var index = block.Statements.IndexOf(declaration);
        if (index < 0) return null;

        var names = new Dictionary<int, string>();
        StatementSyntax? last = null;

        for (var next = index + 1; next < block.Statements.Count; next++)
        {
            if (block.Statements[next] is not LocalDeclarationStatementSyntax following) break;
            if (following.Modifiers.Any() || following.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) break;

            var element = following.Declaration.Variables.SingleOrNone();
            if (element?.Initializer?.Value is not MemberAccessExpressionSyntax member) break;
            if (member.Expression is not IdentifierNameSyntax target || target.Identifier.ValueText != name) break;

            var elementName = member.Name.Identifier.ValueText;
            if (!elementName.StartsWith("Item", StringComparison.Ordinal)) break;
            if (!int.TryParse(elementName.AsSpan("Item".Length), out var position)) break;
            if (position < 1 || position > tuple.TupleElements.Length) break;
            if (names.ContainsKey(position)) break;

            // The local must hold exactly the element: `object a = t.Item1` cannot become `var (a, …)`.
            if (!following.Declaration.Type.IsVar &&
                !SameSymbol(model.GetTypeInfo(following.Declaration.Type, cancellationToken).Type,
                            tuple.TupleElements[position - 1].Type))
            {
                break;
            }

            names[position] = element.Identifier.ValueText;
            last = following;
        }

        // Elements must be a run from Item1: `var (a, _, b)` is not what a partial list means.
        if (names.Count < 2 || last is null) return null;
        if (names.Keys.OrderBy(position => position).Where((position, offset) => position != offset + 1).Any()) return null;

        if (StatementNavigation.IsUsedAfter(last, name)) return null;

        var designation = string.Join(", ", names.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        return MatchShape(declaration, last, $"var ({designation}) = {source.Value};");
    }
}
