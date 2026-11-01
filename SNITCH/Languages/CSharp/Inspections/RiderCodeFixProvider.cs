using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Inspections.Rider;

/// <summary>
/// The repair half of the <c>rider</c> inspection pack: every id the analyzers in
/// <see cref="RiderInspectionAnalyzers"/> report and that has a safe rewrite is fixed here.
/// <c>LoopVariableIsNeverChangedInsideLoop</c> and <c>ConditionIsAlwaysTrueOrFalse</c> report a
/// suspicion with no mechanical repair and are deliberately absent.
/// </summary>
public sealed class RiderCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
    [
        "ConvertToConstant_Local",
        "MemberCanBeMadeStatic_Global",
        "RedundantArgument",
        "EmptyConstructor",
        "RedundantIfElseBlock",
        "RedundantBaseConstructorCall",
        "ConvertToCompoundAssignment",
        "RedundantLambdaSignatureParentheses",
        "UseNameofExpression",
        "StringLiteralAsInterpolationArgument",
        "RedundantCheckBeforeAssignment",
        "RedundantStringInterpolation",
        "ReplaceSequenceEqualWithConstantPattern",
        "RedundantPatternParentheses",
        "MergeIntoPattern",
        "ReplaceWithStringIsNullOrEmpty",
    ];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics[0];
        var title = Title(diagnostic.Id);
        if (title is null) return;

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
        if (root is null) return;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        // Removing a positional argument is only sound when nothing after it is positional.
        if (diagnostic.Id == "RedundantArgument" && !CanRemoveArgument(node)) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title,
                cancellationToken => ApplyAsync(context.Document, diagnostic, cancellationToken),
                diagnostic.Id),
            diagnostic);
    }

    private static string? Title(string id) => id switch
    {
        "ConvertToConstant_Local" => "Convert to const",
        "MemberCanBeMadeStatic_Global" => "Make the method static",
        "RedundantArgument" => "Remove the redundant argument",
        "EmptyConstructor" => "Remove the empty constructor",
        "RedundantIfElseBlock" => "Replace the if/else with a return of the condition",
        "RedundantBaseConstructorCall" => "Remove the base() call",
        "ConvertToCompoundAssignment" => "Convert to compound assignment",
        "RedundantLambdaSignatureParentheses" => "Remove the redundant parentheses",
        "UseNameofExpression" => "Use nameof",
        "StringLiteralAsInterpolationArgument" => "Merge the literal into the interpolated string",
        "RedundantCheckBeforeAssignment" => "Remove the redundant check",
        "RedundantStringInterpolation" => "Remove the string interpolation",
        "ReplaceSequenceEqualWithConstantPattern" => "Replace with a constant pattern",
        "RedundantPatternParentheses" => "Remove the redundant parentheses",
        "MergeIntoPattern" => "Merge the checks into one pattern",
        "ReplaceWithStringIsNullOrEmpty" => "Use string.IsNullOrEmpty",
        _ => null,
    };

    private static async Task<Document> ApplyAsync(
        Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return document;

        var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        return await (diagnostic.Id switch
        {
            "ConvertToConstant_Local" => ConvertToConstantAsync(document, root, node, diagnostic, cancellationToken),
            "MemberCanBeMadeStatic_Global" => ReplaceAsync(document, root,
                node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault(),
                method => method.AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space)),
                cancellationToken),
            "RedundantArgument" => RemoveArgumentAsync(document, root, node, cancellationToken),
            "EmptyConstructor" => RemoveConstructorAsync(document, root, node, cancellationToken),
            "RedundantIfElseBlock" => ReplaceIfElseAsync(document, root, node, cancellationToken),
            "RedundantBaseConstructorCall" => ReplaceAsync(document, root,
                node.AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().FirstOrDefault(),
                constructor => constructor.WithInitializer(null),
                cancellationToken),
            "ConvertToCompoundAssignment" => ConvertToCompoundAssignmentAsync(document, root, node, cancellationToken),
            "RedundantLambdaSignatureParentheses" => RemoveLambdaParenthesesAsync(document, root, node, cancellationToken),
            "UseNameofExpression" => UseNameofAsync(document, root, node, cancellationToken),
            "StringLiteralAsInterpolationArgument" => MergeInterpolationLiteralAsync(document, root, node, cancellationToken),
            "RedundantCheckBeforeAssignment" => RemoveRedundantCheckAsync(document, root, node, cancellationToken),
            "RedundantStringInterpolation" => RemoveStringInterpolationAsync(document, root, node, cancellationToken),
            "ReplaceSequenceEqualWithConstantPattern" => ReplaceSequenceEqualAsync(document, root, node, cancellationToken),
            "RedundantPatternParentheses" => RemovePatternParenthesesAsync(document, root, node, cancellationToken),
            "MergeIntoPattern" => MergeIntoPatternAsync(document, root, node, cancellationToken),
            "ReplaceWithStringIsNullOrEmpty" => ReplaceWithIsNullOrEmptyAsync(document, root, node, cancellationToken),
            _ => Task.FromResult(document),
        });
    }

    private static async Task<Document> ConvertToConstantAsync(
        Document document, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var declaration = node.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (declaration is null) return document;

        var variable = declaration.Declaration.Variables
            .FirstOrDefault(candidate => candidate.Identifier.Span.Contains(diagnostic.Location.SourceSpan));
        if (variable is null) return document;

        var type = declaration.Declaration.Type;
        if (type.IsVar)
        {
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model?.GetDeclaredSymbol(variable, cancellationToken) is not ILocalSymbol local) return document;
            type = SyntaxFactory.ParseTypeName(local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                .WithTriviaFrom(type);
        }

        var updated = declaration
            .WithModifiers(declaration.Modifiers.Add(
                SyntaxFactory.Token(SyntaxKind.ConstKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
            .WithDeclaration(declaration.Declaration.WithType(type));
        return document.WithSyntaxRoot(root.ReplaceNode(declaration, updated));
    }

    private static Task<Document> RemoveArgumentAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var argument = node.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argument?.Parent is not BaseArgumentListSyntax list) return Task.FromResult(document);
        if (!CanRemoveArgument(node)) return Task.FromResult(document);

        var updated = list.RemoveNode(argument, SyntaxRemoveOptions.KeepNoTrivia);
        return updated is null ? Task.FromResult(document) : WithNodeAsync(document, root, list, updated, cancellationToken);
    }

    /// <summary>An argument can go only when it is last or everything after it is named.</summary>
    private static bool CanRemoveArgument(SyntaxNode node)
    {
        var argument = node.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argument?.Parent is not BaseArgumentListSyntax list) return false;

        var index = list.Arguments.IndexOf(argument);
        if (index < 0) return false;

        for (var next = index + 1; next < list.Arguments.Count; next++)
            if (list.Arguments[next].NameColon is null)
                return false;

        return true;
    }

    private static Task<Document> RemoveConstructorAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var constructor = node.AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (constructor?.Parent is null) return Task.FromResult(document);

        var updated = constructor.Parent.RemoveNode(constructor, SyntaxRemoveOptions.KeepNoTrivia);
        return updated is null ? Task.FromResult(document) : WithNodeAsync(document, root, constructor.Parent, updated, cancellationToken);
    }

    private static Task<Document> ReplaceIfElseAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var statement = node.AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();
        if (statement is null) return Task.FromResult(document);
        if (!TryBooleanReturn(statement.Statement, out var whenTrue)) return Task.FromResult(document);

        var condition = statement.Condition.WithoutTrivia();
        var expression = whenTrue
            ? condition
            : SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(condition));
        var replacement = SyntaxFactory.ReturnStatement(expression).WithTriviaFrom(statement);
        return WithNodeAsync(document, root, statement, replacement, cancellationToken);
    }

    private static bool TryBooleanReturn(StatementSyntax statement, out bool value)
    {
        value = false;
        var returned = statement switch
        {
            ReturnStatementSyntax single => single.Expression,
            BlockSyntax { Statements: [ReturnStatementSyntax only] } => only.Expression,
            _ => null,
        };

        if (returned is not LiteralExpressionSyntax literal) return false;
        if (literal.IsKind(SyntaxKind.TrueLiteralExpression)) { value = true; return true; }
        if (literal.IsKind(SyntaxKind.FalseLiteralExpression)) { value = false; return true; }
        return false;
    }

    private static Task<Document> ConvertToCompoundAssignmentAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var assignment = node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        if (assignment?.Right is not BinaryExpressionSyntax binary) return Task.FromResult(document);
        if (!ConvertToCompoundAssignmentAnalyzer.CompoundTokens.TryGetValue(binary.Kind(), out var token))
            return Task.FromResult(document);

        var updated = assignment
            .WithOperatorToken(SyntaxFactory.Token(token).WithTriviaFrom(assignment.OperatorToken))
            .WithRight(binary.Right.WithoutLeadingTrivia().WithTrailingTrivia(assignment.Right.GetTrailingTrivia()));
        return WithNodeAsync(document, root, assignment, updated, cancellationToken);
    }

    private static Task<Document> RemoveLambdaParenthesesAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var lambda = node.AncestorsAndSelf().OfType<ParenthesizedLambdaExpressionSyntax>().FirstOrDefault();
        if (lambda?.ParameterList.Parameters.Count != 1) return Task.FromResult(document);

        var parameter = lambda.ParameterList.Parameters[0];
        var updated = (lambda.Body is BlockSyntax block
                ? SyntaxFactory.SimpleLambdaExpression(parameter, block)
                : SyntaxFactory.SimpleLambdaExpression(parameter, (ExpressionSyntax)lambda.Body))
            .WithAsyncKeyword(lambda.AsyncKeyword)
            .WithArrowToken(SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken)
                .WithLeadingTrivia(SyntaxFactory.Space)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .WithTriviaFrom(lambda);
        return WithNodeAsync(document, root, lambda, updated, cancellationToken);
    }

    private static Task<Document> UseNameofAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var literal = node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal is null) return Task.FromResult(document);

        var name = literal.Token.ValueText;
        if (name.Length == 0) return Task.FromResult(document);

        var replacement = SyntaxFactory.ParseExpression("nameof(" + name + ")").WithTriviaFrom(literal);
        return WithNodeAsync(document, root, literal, replacement, cancellationToken);
    }

    private static Task<Document> MergeInterpolationLiteralAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var interpolation = node.AncestorsAndSelf().OfType<InterpolationSyntax>().FirstOrDefault();
        if (interpolation?.Expression is not LiteralExpressionSyntax literal) return Task.FromResult(document);

        var text = literal.Token.Text;
        if (!text.StartsWith("\"", StringComparison.Ordinal) || text.Length < 2) return Task.FromResult(document);

        var inner = text[1..^1].Replace("{", "{{").Replace("}", "}}");
        var token = SyntaxFactory.Token(
            SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken, inner, literal.Token.ValueText,
            SyntaxTriviaList.Empty);
        var replacement = SyntaxFactory.InterpolatedStringText(token).WithTriviaFrom(interpolation);
        return WithNodeAsync(document, root, interpolation, replacement, cancellationToken);
    }

    private static Task<Document> RemoveRedundantCheckAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var statement = node.AncestorsAndSelf().OfType<IfStatementSyntax>().FirstOrDefault();
        if (statement is null) return Task.FromResult(document);

        var replacement = statement.Statement.WithTriviaFrom(statement);
        return WithNodeAsync(document, root, statement, replacement, cancellationToken);
    }

    private static Task<Document> RemoveStringInterpolationAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var interpolated = node.AncestorsAndSelf().OfType<InterpolatedStringExpressionSyntax>().FirstOrDefault();
        if (interpolated is null) return Task.FromResult(document);
        if (interpolated.Contents.Any(content => content is InterpolationSyntax)) return Task.FromResult(document);

        var value = string.Concat(
            interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(text => text.TextToken.ValueText));
        var source = SymbolDisplay.FormatLiteral(value, quote: true);
        var replacement = SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(source, value)).WithTriviaFrom(interpolated);
        return WithNodeAsync(document, root, interpolated, replacement, cancellationToken);
    }

    private static Task<Document> ReplaceSequenceEqualAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.Expression is not MemberAccessExpressionSyntax member) return Task.FromResult(document);
        if (invocation.ArgumentList.Arguments.Count != 1) return Task.FromResult(document);

        var literal = invocation.ArgumentList.Arguments[0].Expression;
        var qualifier = member.Expression.WithoutTrivia();
        if (qualifier is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or ThisExpressionSyntax
            or ParenthesizedExpressionSyntax))
            qualifier = SyntaxFactory.ParenthesizedExpression(qualifier);

        var replacement = SyntaxFactory.IsPatternExpression(qualifier, SyntaxFactory.ConstantPattern(literal.WithoutTrivia()))
            .WithTriviaFrom(invocation);
        return WithNodeAsync(document, root, invocation, replacement, cancellationToken);
    }

    private static Task<Document> RemovePatternParenthesesAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var parenthesized = node.AncestorsAndSelf().OfType<ParenthesizedPatternSyntax>().FirstOrDefault();
        if (parenthesized?.Pattern is not { } pattern) return Task.FromResult(document);

        return WithNodeAsync(document, root, parenthesized, pattern.WithTriviaFrom(parenthesized), cancellationToken);
    }

    private static Task<Document> MergeIntoPatternAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var binary = node.AncestorsAndSelf().OfType<BinaryExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.IsKind(SyntaxKind.LogicalAndExpression));
        if (binary is null) return Task.FromResult(document);
        if (!MergeIntoPatternAnalyzer.TryMatch(binary, out var left, out var right)) return Task.FromResult(document);

        var pattern = SyntaxFactory.BinaryPattern(
            SyntaxKind.AndPattern,
            left.Pattern.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.Space),
            right.Pattern.WithoutLeadingTrivia());
        var replacement = SyntaxFactory.IsPatternExpression(left.Expression.WithoutTrivia(), pattern)
            .WithTriviaFrom(binary);
        return WithNodeAsync(document, root, binary, replacement, cancellationToken);
    }

    private static async Task<Document> ReplaceWithIsNullOrEmptyAsync(
        Document document, SyntaxNode root, SyntaxNode node, CancellationToken cancellationToken)
    {
        var binary = node.AncestorsAndSelf().OfType<BinaryExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.IsKind(SyntaxKind.LogicalOrExpression) ||
                                         candidate.IsKind(SyntaxKind.LogicalAndExpression));
        if (binary is null) return document;

        var model = await document.GetSemanticModelAsync(cancellationToken);
        var negated = binary.IsKind(SyntaxKind.LogicalAndExpression);
        if (!ReplaceWithStringIsNullOrEmptyAnalyzer.TryMatch(binary, model, negated, out var operand))
            return document;

        ExpressionSyntax call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                SyntaxFactory.IdentifierName("IsNullOrEmpty")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(operand.WithoutTrivia()))));

        if (negated)
            call = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, call);

        return await WithNodeAsync(document, root, binary, call.WithTriviaFrom(binary), cancellationToken);
    }

    private static Task<Document> ReplaceAsync<TSyntax>(
        Document document, SyntaxNode root, TSyntax? original, Func<TSyntax, TSyntax> update,
        CancellationToken cancellationToken)
        where TSyntax : SyntaxNode
        => original is null
            ? Task.FromResult(document)
            : WithNodeAsync(document, root, original, update(original), cancellationToken);

    private static Task<Document> WithNodeAsync(
        Document document, SyntaxNode root, SyntaxNode original, SyntaxNode replacement,
        CancellationToken cancellationToken)
    {
        if (root.ReplaceNode(original, replacement) is not { } updated) return Task.FromResult(document);
        return Task.FromResult(document.WithSyntaxRoot(updated));
    }
}
