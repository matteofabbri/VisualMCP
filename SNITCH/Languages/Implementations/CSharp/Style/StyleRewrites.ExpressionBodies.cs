using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// The expression-body family (IDE0021–IDE0027, IDE0053, IDE0061). All of them are the same move — a
/// body that is one statement becomes <c>=> statement</c> — so the shape check and the pieces of the
/// replacement live here and each rule only says which declaration it applies to.
/// </summary>
internal static class ExpressionBodies
{
    /// <summary>
    /// The expression a block body stands for, or null when the shape is not a clean one-statement
    /// body. A block holding a comment or a directive is refused: the rewrite would delete it.
    /// </summary>
    public static ExpressionSyntax? Expression(BlockSyntax body)
    {
        if (body.Statements.Count != 1) return null;
        if (HasCommentOrDirective(body)) return null;

        return body.Statements[0] switch
        {
            ReturnStatementSyntax { Expression: { } returned } => returned,
            ExpressionStatementSyntax { Expression: { } statement } => statement,
            ThrowStatementSyntax { Expression: { } thrown } =>
                ThrowExpression(Token(SyntaxKind.ThrowKeyword).WithTrailingTrivia(Space), thrown),
            _ => null,
        };
    }

    public static ArrowExpressionClauseSyntax Arrow(ExpressionSyntax expression) =>
        ArrowExpressionClause(
            Token(SyntaxKind.EqualsGreaterThanToken).WithTrailingTrivia(Space),
            expression.WithoutLeadingTrivia());

    /// <summary>The semicolon that closes an expression body, carrying the removed block's line break.</summary>
    public static SyntaxToken Semicolon(SyntaxNode removedBody) =>
        Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(removedBody.GetTrailingTrivia());

    private static bool HasCommentOrDirective(SyntaxNode node) =>
        node.DescendantTrivia(descendIntoTrivia: true).Any(trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.DisabledTextTrivia));
}

/// <summary>IDE0022: <c>int F() { return 1; }</c> → <c>int F() => 1;</c>.</summary>
public sealed class MethodExpressionBodyRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0022", "Use expression body for methods",
        "int F() { return 1; } → int F() => 1;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MethodDeclarationSyntax method || method.Body is null) return null;
        if (ExpressionBodies.Expression(method.Body) is not { } expression) return null;

        var replacement = method
            .WithBody(null)
            .WithExpressionBody(ExpressionBodies.Arrow(expression))
            .WithSemicolonToken(ExpressionBodies.Semicolon(method.Body));

        return Match(node, replacement);
    }
}

/// <summary>IDE0021: <c>C(int a) { Age = a; }</c> → <c>C(int a) => Age = a;</c>.</summary>
public sealed class ConstructorExpressionBodyRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0021", "Use expression body for constructors",
        "C(int a) { Age = a; } → C(int a) => Age = a;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ConstructorDeclarationSyntax constructor || constructor.Body is null) return null;

        // A static constructor cannot be expression-bodied.
        if (constructor.Modifiers.Any(SyntaxKind.StaticKeyword)) return null;
        if (ExpressionBodies.Expression(constructor.Body) is not { } expression) return null;

        var replacement = constructor
            .WithBody(null)
            .WithExpressionBody(ExpressionBodies.Arrow(expression))
            .WithSemicolonToken(ExpressionBodies.Semicolon(constructor.Body));

        return Match(node, replacement);
    }
}

/// <summary>IDE0023 / IDE0024: a user-defined operator with a one-statement body.</summary>
public sealed class OperatorExpressionBodyRewrite : CSharpStyleRewrite
{
    private static readonly StyleRule Conversion = new(
        "IDE0023", "Use expression body for conversion operators",
        "public static implicit operator int(C c) { return c.Value; } → … => c.Value;");

    private static readonly StyleRule Operator = new(
        "IDE0024", "Use expression body for operators",
        "public static C operator +(C a, C b) { return a; } → … => a;");

    public override StyleRule Rule => Operator;

    public override IReadOnlyList<StyleRule> Rules => [Conversion, Operator];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case ConversionOperatorDeclarationSyntax conversion when conversion.Body is not null:
                if (ExpressionBodies.Expression(conversion.Body) is not { } converted) return null;

                return Match(Conversion, node, conversion
                    .WithBody(null)
                    .WithExpressionBody(ExpressionBodies.Arrow(converted))
                    .WithSemicolonToken(ExpressionBodies.Semicolon(conversion.Body)));

            case OperatorDeclarationSyntax declared when declared.Body is not null:
                if (ExpressionBodies.Expression(declared.Body) is not { } result) return null;

                return Match(Operator, node, declared
                    .WithBody(null)
                    .WithExpressionBody(ExpressionBodies.Arrow(result))
                    .WithSemicolonToken(ExpressionBodies.Semicolon(declared.Body)));

            default:
                return null;
        }
    }
}

/// <summary>IDE0025 / IDE0026: a get-only property or indexer whose getter returns one expression.</summary>
public sealed class PropertyExpressionBodyRewrite : CSharpStyleRewrite
{
    private static readonly StyleRule Property = new(
        "IDE0025", "Use expression body for properties",
        "public int P { get { return _p; } } → public int P => _p;");

    private static readonly StyleRule Indexer = new(
        "IDE0026", "Use expression body for indexers",
        "public int this[int i] { get { return _a[i]; } } → public int this[int i] => _a[i];");

    public override StyleRule Rule => Property;

    public override IReadOnlyList<StyleRule> Rules => [Property, Indexer];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        return node switch
        {
            PropertyDeclarationSyntax declaration when GetOnlyBody(declaration.AccessorList, declaration.Initializer) is { } expression =>
                Match(Property, node, declaration
                    .WithAccessorList(null)
                    .WithExpressionBody(ExpressionBodies.Arrow(expression))
                    .WithSemicolonToken(ExpressionBodies.Semicolon(declaration.AccessorList!))),

            IndexerDeclarationSyntax indexer when GetOnlyBody(indexer.AccessorList, initializer: null) is { } expression =>
                Match(Indexer, node, indexer
                    .WithAccessorList(null)
                    .WithExpressionBody(ExpressionBodies.Arrow(expression))
                    .WithSemicolonToken(ExpressionBodies.Semicolon(indexer.AccessorList!))),

            _ => null,
        };
    }

    /// <summary>
    /// The expression of a single getter with a one-statement body. An initializer keeps the property a
    /// field-like declaration, so it is refused; a setter or init makes the arrow form invalid.
    /// </summary>
    private static ExpressionSyntax? GetOnlyBody(AccessorListSyntax? accessors, EqualsValueClauseSyntax? initializer)
    {
        if (accessors is null || initializer is not null) return null;
        if (accessors.Accessors.Count != 1) return null;

        var getter = accessors.Accessors[0];
        if (!getter.IsKind(SyntaxKind.GetAccessorDeclaration) || getter.Body is null) return null;

        return ExpressionBodies.Expression(getter.Body);
    }
}

/// <summary>IDE0027: <c>get { return _p; }</c> → <c>get => _p;</c> inside a property with several accessors.</summary>
public sealed class AccessorExpressionBodyRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0027", "Use expression body for accessors",
        "get { return _p; } → get => _p;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not AccessorDeclarationSyntax accessor || accessor.Body is null) return null;
        if (ExpressionBodies.Expression(accessor.Body) is not { } expression) return null;

        var replacement = accessor
            .WithBody(null)
            .WithExpressionBody(ExpressionBodies.Arrow(expression))
            .WithSemicolonToken(ExpressionBodies.Semicolon(accessor.Body));

        return Match(node, replacement);
    }
}

/// <summary>IDE0053: a lambda whose block body is one statement.</summary>
public sealed class LambdaExpressionBodyRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0053", "Use expression body for lambdas",
        "x => { return x + 1; } → x => x + 1");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LambdaExpressionSyntax lambda || lambda.Block is not { } body) return null;
        if (ExpressionBodies.Expression(body) is not { } expression) return null;

        // A lambda has no semicolon token of its own: the statement that contains it keeps its own.
        return Match(node, lambda.WithBlock(null).WithExpressionBody(expression.WithoutLeadingTrivia()));
    }
}

/// <summary>IDE0061: a local function whose block body is one statement.</summary>
public sealed class LocalFunctionExpressionBodyRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0061", "Use expression body for local functions",
        "int F() { return 1; } → int F() => 1;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalFunctionStatementSyntax local || local.Body is null) return null;
        if (ExpressionBodies.Expression(local.Body) is not { } expression) return null;

        var replacement = local
            .WithBody(null)
            .WithExpressionBody(ExpressionBodies.Arrow(expression))
            .WithSemicolonToken(ExpressionBodies.Semicolon(local.Body));

        return Match(node, replacement);
    }
}
