using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.Style;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// The C# half of a style rewrite: <see cref="StyleRewrite"/> plus the helpers that need C#'s own syntax
/// factory (parsing a replacement expression or statement from text, guessing whether a receiver is
/// safe to write twice). A rule that only needs nodes and the semantic model can derive from the shared
/// base directly; every rule in this folder needs at least one of these.
/// </summary>
public abstract class CSharpStyleRewrite : Snitch.Languages.Style.StyleRewrite
{
    protected StyleMatch Match(SyntaxNode node, string expressionText) =>
        Match(node, ParseExpression(expressionText).WithTriviaFrom(node));

    /// <summary>
    /// A multi-statement shape: <paramref name="first"/> is what the service will replace (and what
    /// TryMatch matched on), <paramref name="last"/> the statement where the shape ends. The text is
    /// parsed as a block so no statement is dropped (ParseStatement keeps only the first one); the
    /// service splices the block's statements back into the enclosing list.
    /// </summary>
    protected StyleMatch MatchShape(StyleRule rule, SyntaxNode first, SyntaxNode last, string statementsText) =>
        new(rule, first, ParseStatement($"{{ {statementsText} }}"), LineOf(first),
            first.ToString(), statementsText, Last: last);

    /// <summary>A multi-statement shape whose replacement text stands for the whole shape.</summary>
    protected StyleMatch MatchShape(SyntaxNode first, SyntaxNode last, string statementsText) =>
        MatchShape(Rule, first, last, statementsText);

    protected StyleMatch MatchStatement(SyntaxNode node, string statementText) =>
        Match(node, ParseStatement(statementText).WithTriviaFrom(node));

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// Whether writing <paramref name="nameText"/> at <paramref name="at"/> would bind to
    /// <paramref name="target"/>. Speculative binding applies the real scoping rules, so a local that
    /// shadows the member makes the answer "no" — which a lookup by name alone would get wrong.
    /// </summary>
    protected static bool BindsTo(SemanticModel model, SyntaxNode at, string nameText, ISymbol? target, bool asType)
    {
        if (target is null) return false;

        var info = asType
            ? model.GetSpeculativeSymbolInfo(at.SpanStart, ParseTypeName(nameText), SpeculativeBindingOption.BindAsTypeOrNamespace)
            : model.GetSpeculativeSymbolInfo(at.SpanStart, ParseExpression(nameText), SpeculativeBindingOption.BindAsExpression);

        return SameSymbol(info.Symbol, target);
    }

    /// <summary>True when the node is a simple receiver that can be written twice without side effects.</summary>
    protected static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        ThisExpressionSyntax => true,
        LiteralExpressionSyntax => true,
        MemberAccessExpressionSyntax member => IsSideEffectFree(member.Expression),
        _ => false,
    };

    protected static bool IsNullLiteral(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.NullLiteralExpression) ||
        expression.IsKind(SyntaxKind.DefaultLiteralExpression);

    /// <summary>
    /// Elements of a collection expression, comma separated with the usual single space. The braces'
    /// inner padding ("{ 1, 2 }") is dropped from the edges so the result reads "[1, 2]", unless the
    /// whitespace is a line break (a multi-line initializer keeps its layout).
    /// </summary>
    protected static SeparatedSyntaxList<CollectionElementSyntax> SpacedElements(SeparatedSyntaxList<ExpressionSyntax> expressions)
    {
        var elements = new List<CollectionElementSyntax>();
        for (var index = 0; index < expressions.Count; index++)
        {
            var expression = expressions[index];
            var element = SyntaxFactory.ExpressionElement(expression);

            if (index == 0 && IsInlineWhitespace(expression.GetLeadingTrivia()))
                element = element.WithoutLeadingTrivia();

            if (index == expressions.Count - 1 && IsInlineWhitespace(expression.GetTrailingTrivia()))
                element = element.WithoutTrailingTrivia();

            if (index > 0 && !expression.HasLeadingTrivia)
                element = element.WithLeadingTrivia(Space);

            elements.Add(element);
        }

        return SeparatedList(elements);
    }

    private static bool IsInlineWhitespace(SyntaxTriviaList trivia) =>
        trivia.Count > 0 && trivia.All(item => item.IsKind(SyntaxKind.WhitespaceTrivia));
}

// ── collections and object creation ─────────────────────────────────────────
/// <summary>IDE0028 / IDE0300 / IDE0301: a collection built with new and filled → a collection expression.</summary>
public sealed class CollectionExpressionRewrite : CSharpStyleRewrite
{
    public static readonly StyleRule CollectionRule = new(
        "IDE0028", "Collection initialization can be simplified",
        "new List<T>() → [], new List<T> { a, b } → [a, b]");

    public static readonly StyleRule ArrayRule = new(
        "IDE0300", "Use collection expression for array",
        "new T[] { a, b } → [a, b], new[] { a, b } → [a, b]");

    public static readonly StyleRule EmptyArrayRule = new(
        "IDE0301", "Use collection expression for empty",
        "new T[0] → []");

    public override StyleRule Rule => CollectionRule;

    public override IReadOnlyList<StyleRule> Rules => [CollectionRule, ArrayRule, EmptyArrayRule];

    /// <summary>
    /// Collection types whose parameterless constructor plus initializer means exactly what a collection
    /// expression means. Curated on purpose: a user type's Add may do more than append, and dictionaries
    /// take KeyValuePair elements rather than initializers.
    /// </summary>
    private static readonly HashSet<string> CollectionTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List<T>",
        "System.Collections.Generic.LinkedList<T>",
        "System.Collections.Generic.Queue<T>",
        "System.Collections.Generic.Stack<T>",
        "System.Collections.Generic.HashSet<T>",
        "System.Collections.Generic.SortedSet<T>",
        "System.Collections.ObjectModel.Collection<T>",
        "System.Collections.Concurrent.ConcurrentBag<T>",
        "System.Collections.Concurrent.ConcurrentQueue<T>",
        "System.Collections.Concurrent.ConcurrentStack<T>",
    };

    private const string EnumerableOfT = "System.Collections.Generic.IEnumerable<T>";

    /// <summary>Whether IDE0090 must leave this type alone because IDE0028 already handles it.</summary>
    public static bool Handles(ITypeSymbol? type) => IsSupportedCollectionType(type);

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        // The context must be able to hold a collection expression: an array or an IEnumerable<T>.
        if (!IsCollectionTarget(model.GetTypeInfo(node, cancellationToken).ConvertedType)) return null;

        switch (node)
        {
            case ObjectCreationExpressionSyntax creation when
                (creation.ArgumentList is null || creation.ArgumentList.Arguments.Count == 0) &&
                IsSupportedCollectionType(model.GetTypeInfo(creation, cancellationToken).Type):
            {
                if (creation.Initializer is null)
                    return Match(CollectionRule, node, EmptyCollection().WithTriviaFrom(node));

                if (!creation.Initializer.IsKind(SyntaxKind.CollectionInitializerExpression)) return null;
                if (creation.Initializer.Expressions.Any(element => element is InitializerExpressionSyntax)) return null;

                return Match(CollectionRule, node,
                    Collection(SpacedElements(creation.Initializer.Expressions)).WithTriviaFrom(node));
            }

            case ArrayCreationExpressionSyntax array when array.Type.RankSpecifiers.Count == 1 &&
                                                          array.Type.RankSpecifiers[0].Rank == 1:
            {
                if (array.Initializer is not null)
                    return Match(ArrayRule, node,
                        Collection(SpacedElements(array.Initializer.Expressions)).WithTriviaFrom(node));

                var sizes = array.Type.RankSpecifiers[0].Sizes;
                if (sizes.Count == 1 && sizes[0] is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.NumericLiteralExpression) && literal.Token.ValueText == "0")
                {
                    return Match(EmptyArrayRule, node, EmptyCollection().WithTriviaFrom(node));
                }

                return null;
            }

            case ImplicitArrayCreationExpressionSyntax implicitArray when implicitArray.Initializer.Expressions.Count > 0:
                return Match(ArrayRule, node,
                    Collection(SpacedElements(implicitArray.Initializer.Expressions)).WithTriviaFrom(node));

            default:
                return null;
        }
    }

    private static bool IsSupportedCollectionType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && CollectionTypes.Contains(named.OriginalDefinition.ToDisplayString());

    private static bool IsCollectionTarget(ITypeSymbol? converted) =>
        converted is IArrayTypeSymbol ||
        converted is INamedTypeSymbol named &&
        (named.OriginalDefinition.ToDisplayString() == EnumerableOfT ||
         named.AllInterfaces.Any(face => face.OriginalDefinition.ToDisplayString() == EnumerableOfT));

    private static CollectionExpressionSyntax EmptyCollection() => CollectionExpression();

    private static CollectionExpressionSyntax Collection(SeparatedSyntaxList<CollectionElementSyntax> elements) =>
        CollectionExpression(elements);
}

/// <summary>IDE0090: <c>new Foo(args)</c> → <c>new(args)</c> when the context already fixes the type.</summary>
public sealed class TargetTypedNewRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0090", "'new' expression can be simplified",
        "new Foo(args) → new(args) when the context fixes the type");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ObjectCreationExpressionSyntax creation) return null;
        if (creation.ArgumentList is null) return null;
        if (creation.Initializer is not null) return null;

        var typeInfo = model.GetTypeInfo(creation, cancellationToken);
        if (typeInfo.Type is null || typeInfo.ConvertedType is null) return null;
        if (!SymbolEqualityComparer.Default.Equals(typeInfo.Type, typeInfo.ConvertedType)) return null;

        // `var x = new Foo()` and `var x = new()` are not the same: the second cannot infer a type.
        if (IsVarInitializer(creation)) return null;

        // A collection belongs to IDE0028, where "[]" reads better than "new()".
        if (CollectionExpressionRewrite.Handles(typeInfo.Type)) return null;

        var replacement = ImplicitObjectCreationExpression(
            creation.ArgumentList.WithoutTrivia(), initializer: null).WithTriviaFrom(creation);

        return Match(node, replacement);
    }

    private static bool IsVarInitializer(ObjectCreationExpressionSyntax creation) =>
        creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } &&
        declaration.Type.IsVar;
}

// ── qualification ───────────────────────────────────────────────────────────
/// <summary>IDE0001: a qualified name whose last part already binds on its own → the last part.</summary>
public sealed class SimplifyNameRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0001", "Simplify name", "System.Collections.Generic.List<int> → List<int> when the short name binds",
        ClaimsEquivalence: true);

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not QualifiedNameSyntax qualified) return null;
        if (qualified.Parent is QualifiedNameSyntax) return null;

        var symbol = model.GetSymbolInfo(qualified, cancellationToken).Symbol;
        if (symbol is null) return null;
        if (!BindsTo(model, qualified, qualified.Right.ToString(), symbol, asType: true)) return null;

        return Match(node, qualified.Right.WithTriviaFrom(qualified));
    }
}

/// <summary>IDE0002: <c>Type.StaticMember</c> → <c>StaticMember</c> when the unqualified name binds.</summary>
public sealed class SimplifyMemberAccessRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0002", "Simplify member access", "Type.Member → Member when the unqualified name binds",
        ClaimsEquivalence: true);

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax memberAccess) return null;
        if (model.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol is not INamedTypeSymbol) return null;

        var symbol = model.GetSymbolInfo(memberAccess, cancellationToken).Symbol;
        if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or INamedTypeSymbol)) return null;
        if (!BindsTo(model, memberAccess, memberAccess.Name.ToString(), symbol, asType: false)) return null;

        return Match(node, memberAccess.Name.WithTriviaFrom(memberAccess));
    }
}

/// <summary>IDE0003: <c>this.Member</c> → <c>Member</c> when nothing shadows it.</summary>
public sealed class RemoveThisQualificationRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0003", "Name can be simplified", "this.Member → Member when the unqualified name binds",
        ClaimsEquivalence: true);

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax memberAccess) return null;
        if (memberAccess.Expression is not ThisExpressionSyntax) return null;

        var symbol = model.GetSymbolInfo(memberAccess, cancellationToken).Symbol;
        if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) return null;
        if (!BindsTo(model, memberAccess, memberAccess.Name.ToString(), symbol, asType: false)) return null;

        return Match(node, memberAccess.Name.WithTriviaFrom(memberAccess));
    }
}

/// <summary>IDE0049: <c>System.Int32</c> or <c>Int32</c> → <c>int</c>.</summary>
public sealed class PredefinedTypeRewrite : CSharpStyleRewrite
{
    private static readonly Dictionary<SpecialType, SyntaxKind> Keywords = new()
    {
        [SpecialType.System_Object] = SyntaxKind.ObjectKeyword,
        [SpecialType.System_String] = SyntaxKind.StringKeyword,
        [SpecialType.System_Boolean] = SyntaxKind.BoolKeyword,
        [SpecialType.System_Char] = SyntaxKind.CharKeyword,
        [SpecialType.System_SByte] = SyntaxKind.SByteKeyword,
        [SpecialType.System_Byte] = SyntaxKind.ByteKeyword,
        [SpecialType.System_Int16] = SyntaxKind.ShortKeyword,
        [SpecialType.System_UInt16] = SyntaxKind.UShortKeyword,
        [SpecialType.System_Int32] = SyntaxKind.IntKeyword,
        [SpecialType.System_UInt32] = SyntaxKind.UIntKeyword,
        [SpecialType.System_Int64] = SyntaxKind.LongKeyword,
        [SpecialType.System_UInt64] = SyntaxKind.ULongKeyword,
        [SpecialType.System_Single] = SyntaxKind.FloatKeyword,
        [SpecialType.System_Double] = SyntaxKind.DoubleKeyword,
        [SpecialType.System_Decimal] = SyntaxKind.DecimalKeyword,
    };

    public override StyleRule Rule { get; } = new(
        "IDE0049", "Use language keywords instead of framework type names",
        "System.Int32 → int, String → string", ClaimsEquivalence: true);

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not NameSyntax name) return null;
        if (node.Parent is QualifiedNameSyntax { Right: var right } && right == node) return null;
        if (node.Parent is AliasQualifiedNameSyntax or UsingDirectiveSyntax) return null;

        // The right part of `A.B` in an expression context is a member access name: a predefined type
        // cannot stand there (`System.String` → `System.string` is not even parsable).
        if (node.Parent is MemberAccessExpressionSyntax { Name: var memberName } && memberName == node) return null;

        if (model.GetSymbolInfo(name, cancellationToken).Symbol is not INamedTypeSymbol type) return null;
        if (!Keywords.TryGetValue(type.SpecialType, out var keyword)) return null;

        return Match(node, PredefinedType(Token(keyword)).WithTriviaFrom(node));
    }
}
