using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

// ── casts, null handling, defaults ──────────────────────────────────────────
/// <summary>IDE0004: a cast to the operand's own type → the operand.</summary>
public sealed class UnnecessaryCastRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0004", "Remove unnecessary cast", "(int)x → x when x already has that type");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not CastExpressionSyntax cast) return null;

        var castType = model.GetTypeInfo(cast, cancellationToken).Type;
        var operandType = model.GetTypeInfo(cast.Expression, cancellationToken).Type;
        if (castType is null || operandType is null) return null;
        if (!SymbolEqualityComparer.Default.Equals(castType, operandType)) return null;

        return Match(node, cast.Expression.WithTriviaFrom(cast));
    }
}

/// <summary>IDE0031: <c>x != null ? x.M : null</c> → <c>x?.M</c>.</summary>
public sealed class NullPropagationRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0031", "Use null propagation", "x != null ? x.M() : null → x?.M()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ConditionalExpressionSyntax conditional) return null;

        // x != null ? access : null      or      x == null ? null : access
        var (receiver, access) = conditional.Condition switch
        {
            _ when IsNullCheck(conditional.Condition, isNull: false) && IsNullLiteral(conditional.WhenFalse)
                => (Other(conditional.Condition), conditional.WhenTrue),
            _ when IsNullCheck(conditional.Condition, isNull: true) && IsNullLiteral(conditional.WhenTrue)
                => (Other(conditional.Condition), conditional.WhenFalse),
            _ => (null, null),
        };

        if (receiver is null || access is null) return null;

        string? text = access switch
        {
            MemberAccessExpressionSyntax member when member.Expression.ToString() == receiver.ToString()
                => $"{receiver}?.{member.Name}",
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation
                when member.Expression.ToString() == receiver.ToString()
                => $"{receiver}?.{member.Name}({string.Join(", ", invocation.ArgumentList.Arguments)})",
            _ => null,
        };

        return text is null ? null : Match(node, text);
    }

    private static bool IsNullCheck(ExpressionSyntax condition, bool isNull) =>
        condition switch
        {
            BinaryExpressionSyntax binary =>
                binary.IsKind(isNull ? SyntaxKind.EqualsExpression : SyntaxKind.NotEqualsExpression) &&
                (IsNullLiteral(binary.Left) || IsNullLiteral(binary.Right)),
            _ => false,
        };

    private static ExpressionSyntax Other(ExpressionSyntax condition) =>
        condition is BinaryExpressionSyntax binary
            ? IsNullLiteral(binary.Left) ? binary.Right : binary.Left
            : condition;
}

/// <summary>IDE0034: <c>default(T)</c> → <c>default</c>.</summary>
public sealed class SimplifyDefaultRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0034", "Simplify default expression", "default(T) → default");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not DefaultExpressionSyntax) return null;

        return Match(node, LiteralExpression(SyntaxKind.DefaultLiteralExpression, Token(SyntaxKind.DefaultKeyword))
            .WithTriviaFrom(node));
    }
}

/// <summary>IDE0041: <c>x == null</c> → <c>x is null</c>, for the built-in reference comparison only.</summary>
public sealed class IsNullRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0041", "Use 'is null' check", "x == null → x is null");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax binary) return null;
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return null;
        if (IsUserDefinedOperator(model, binary, cancellationToken)) return null;

        var (other, isNull) = IsNullLiteral(binary.Right)
            ? (binary.Left, binary.IsKind(SyntaxKind.EqualsExpression))
            : IsNullLiteral(binary.Left)
                ? (binary.Right, binary.IsKind(SyntaxKind.EqualsExpression))
                : (null, false);

        if (other is null) return null;
        if (other is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax or ThisExpressionSyntax)) return null;

        return Match(node, $"{other} {(isNull ? "is" : "is not")} null");
    }
}

// ── grouping and operators ──────────────────────────────────────────────────
/// <summary>IDE0047: parentheses around a primary expression → nothing.</summary>
public sealed class UnnecessaryParenthesesRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0047", "Remove unnecessary parentheses", "(x).M() → x.M()", ClaimsEquivalence: true);

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ParenthesizedExpressionSyntax parenthesized) return null;

        // Only primary expressions: their precedence never depends on the parentheses.
        if (parenthesized.Expression is not (IdentifierNameSyntax or LiteralExpressionSyntax or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax
            or ThisExpressionSyntax or BaseExpressionSyntax)) return null;

        return Match(node, parenthesized.Expression.WithTriviaFrom(parenthesized));
    }
}

/// <summary>IDE0054: <c>x = x + y</c> → <c>x += y</c>.</summary>
public sealed class CompoundAssignmentRewrite : CSharpStyleRewrite
{
    private static readonly HashSet<SyntaxKind> Operators = new()
    {
        SyntaxKind.AddExpression, SyntaxKind.SubtractExpression, SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideExpression, SyntaxKind.ModuloExpression, SyntaxKind.BitwiseAndExpression,
        SyntaxKind.BitwiseOrExpression, SyntaxKind.ExclusiveOrExpression, SyntaxKind.LeftShiftExpression,
    };

    public override StyleRule Rule { get; } = new(
        "IDE0054", "Use compound assignment", "x = x + y → x += y");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not AssignmentExpressionSyntax assignment) return null;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return null;
        if (assignment.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) return null;
        if (assignment.Right is not BinaryExpressionSyntax binary) return null;
        if (!Operators.Contains(binary.Kind())) return null;

        if (binary.Left.ToString() != assignment.Left.ToString()) return null;

        return Match(node,
            $"{assignment.Left} {binary.OperatorToken.Text}= {binary.Right}");
    }
}

/// <summary>IDE0056: <c>a[a.Length - 1]</c> → <c>a[^1]</c>.</summary>
public sealed class IndexOperatorRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0056", "Use index operator", "a[a.Length - 1] → a[^1]");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ElementAccessExpressionSyntax elementAccess) return null;
        if (elementAccess.ArgumentList.Arguments.Count != 1) return null;
        if (elementAccess.ArgumentList.Arguments[0].Expression is not BinaryExpressionSyntax binary) return null;
        if (!binary.IsKind(SyntaxKind.SubtractExpression)) return null;
        if (binary.Left is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Length" } length) return null;
        if (length.Expression.ToString() != elementAccess.Expression.ToString()) return null;
        if (binary.Right is not LiteralExpressionSyntax literal) return null;
        if (!int.TryParse(literal.Token.ValueText, out var offset) || offset < 1) return null;

        var receiver = elementAccess.Expression.ToString();
        return Match(node, $"{receiver}[^{offset}]");
    }
}

/// <summary>IDE0057: <c>s.Substring(a)</c> / <c>s.Substring(a, n)</c> → the range operators.</summary>
public sealed class RangeOperatorRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0057", "Use range operator", "s.Substring(a) → s[a..], s.Substring(0, n) → s[..n]");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax invocation) return null;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Substring" } call)
            return null;

        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method) return null;
        if (method.ContainingType.SpecialType != SpecialType.System_String) return null;

        var arguments = invocation.ArgumentList.Arguments;
        var receiver = call.Expression.ToString();

        if (arguments.Count == 1)
        {
            var start = arguments[0].Expression;
            if (!IsSideEffectFree(start)) return null;
            return Match(node, $"{receiver}[{start}..]");
        }

        if (arguments.Count == 2)
        {
            var start = arguments[0].Expression;
            var length = arguments[1].Expression;
            if (!IsSideEffectFree(start) || !IsSideEffectFree(length)) return null;

            if (start is LiteralExpressionSyntax { Token.ValueText: "0" })
                return Match(node, $"{receiver}[..{length}]");

            return Match(node, $"{receiver}[{start}..({start} + {length})]");
        }

        return null;
    }
}

/// <summary>IDE0074: <c>if (x == null) x = y;</c> → <c>x ??= y;</c>.</summary>
public sealed class CoalesceAssignmentRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0074", "Use coalesce compound assignment", "if (x == null) x = y; → x ??= y;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not IfStatementSyntax ifStatement || ifStatement.Else is not null) return null;
        if (!IsNullCheck(ifStatement.Condition)) return null;
        if (ifStatement.Statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment })
            return null;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return null;

        var checkedExpression = ifStatement.Condition is BinaryExpressionSyntax binary
            ? IsNullLiteral(binary.Left) ? binary.Right : binary.Left
            : ((IsPatternExpressionSyntax)ifStatement.Condition).Expression;

        if (assignment.Left.ToString() != checkedExpression.ToString()) return null;

        return MatchStatement(node, $"{assignment.Left} ??= {assignment.Right};");
    }

    private static bool IsNullCheck(ExpressionSyntax condition) => condition switch
    {
        BinaryExpressionSyntax binary =>
            binary.IsKind(SyntaxKind.EqualsExpression) && IsNullLiteral(binary.Right),
        IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: var pattern } } =>
            IsNullLiteral(pattern) || pattern.IsKind(SyntaxKind.DefaultLiteralExpression),
        _ => false,
    };
}

/// <summary>IDE0083: <c>!(x is T)</c> → <c>x is not T</c>.</summary>
public sealed class NotPatternRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0083", "Use pattern matching ('not' operator)", "!(x is T) → x is not T");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not PrefixUnaryExpressionSyntax prefix) return null;
        if (!prefix.IsKind(SyntaxKind.LogicalNotExpression)) return null;

        // `!(x is T)` is the only way to write it, so the is-expression always sits in parentheses.
        var operand = prefix.Operand is ParenthesizedExpressionSyntax parenthesized
            ? parenthesized.Expression
            : prefix.Operand;

        if (operand is not BinaryExpressionSyntax isExpression) return null;
        if (!isExpression.IsKind(SyntaxKind.IsExpression)) return null;

        // A declaration pattern (`x is T t`) cannot be negated this way.
        if (isExpression.Right is not TypeSyntax type) return null;

        return Match(node, $"{isExpression.Left} is not {type}");
    }
}

/// <summary>IDE0100: <c>x == true</c> → <c>x</c>, <c>x == false</c> → <c>!x</c>.</summary>
public sealed class UnnecessaryEqualityRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0100", "Remove unnecessary equality operator", "x == true → x, x != false → x");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax binary) return null;
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return null;
        if (IsUserDefinedOperator(model, binary, cancellationToken)) return null;

        var (other, literal) = binary.Left is LiteralExpressionSyntax left && IsBoolLiteral(left)
            ? (binary.Right, left)
            : binary.Right is LiteralExpressionSyntax right && IsBoolLiteral(right)
                ? (binary.Left, right)
                : (null, null);

        if (other is null || literal is null) return null;
        if (model.GetTypeInfo(other, cancellationToken).Type?.SpecialType != SpecialType.System_Boolean) return null;

        var negate = (literal.IsKind(SyntaxKind.TrueLiteralExpression) && binary.IsKind(SyntaxKind.NotEqualsExpression)) ||
                     (literal.IsKind(SyntaxKind.FalseLiteralExpression) && binary.IsKind(SyntaxKind.EqualsExpression));

        if (!negate) return Match(node, other.ToString());

        return Match(node, other is BinaryExpressionSyntax ? $"!({other})" : $"!{other}");
    }

    private static bool IsBoolLiteral(LiteralExpressionSyntax literal) =>
        literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression);
}

// ── lambdas, nameof, delegates, interpolation ───────────────────────────────
/// <summary>IDE0200: <c>x => M(x)</c> → <c>M</c>.</summary>
public sealed class UnnecessaryLambdaRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0200", "Remove unnecessary lambda expression", "x => M(x) → M");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LambdaExpressionSyntax lambda) return null;

        var parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter.Identifier.ValueText },
            ParenthesizedLambdaExpressionSyntax parenthesized =>
                parenthesized.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText).ToArray(),
            _ => [],
        };

        if (parameters.Length == 0) return null;
        if (lambda.Body is not InvocationExpressionSyntax invocation) return null;
        if (invocation.ArgumentList.Arguments.Count != parameters.Length) return null;

        for (var index = 0; index < parameters.Length; index++)
        {
            var argument = invocation.ArgumentList.Arguments[index];
            if (argument.NameColon is not null) return null;
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
            if (argument.Expression is not IdentifierNameSyntax identifier) return null;
            if (identifier.Identifier.ValueText != parameters[index]) return null;
        }

        // The method group must not be reached through one of the lambda's own parameters.
        if (invocation.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(identifier => parameters.Contains(identifier.Identifier.ValueText))) return null;

        return Match(node, invocation.Expression.ToString());
    }
}

/// <summary>IDE0280: <c>new ArgumentNullException("name")</c> → <c>nameof(name)</c>.</summary>
public sealed class ArgumentNullNameofRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0280", "Use nameof", "new ArgumentNullException(\"x\") → nameof(x)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ObjectCreationExpressionSyntax creation) return null;
        if (creation.ArgumentList?.Arguments.Count != 1) return null;
        if (model.GetTypeInfo(creation, cancellationToken).Type is not INamedTypeSymbol { Name: "ArgumentNullException" })
            return null;

        var argument = creation.ArgumentList.Arguments[0];
        if (argument.Expression is not LiteralExpressionSyntax literal) return null;
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) return null;

        var text = literal.Token.ValueText;
        if (!SyntaxFacts.IsValidIdentifier(text)) return null;

        // Only when the name exists as a parameter of the enclosing method: that is the case the IDE
        // reports, and it keeps the rewrite free of guessing.
        var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (method is null) return null;
        if (!method.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == text)) return null;

        // The matched node is the literal, not the ArgumentSyntax: the replacement must stay an
        // expression, or replacing the argument would break the argument list.
        return Match(argument.Expression, $"nameof({text})");
    }
}

/// <summary>IDE0340: <c>nameof(List&lt;int&gt;)</c> → <c>nameof(List&lt;&gt;)</c>.</summary>
public sealed class UnboundGenericNameofRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0340", "Use unbound generic type", "nameof(List<int>) → nameof(List<>)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax invocation) return null;
        if (invocation.Expression is not IdentifierNameSyntax { Identifier.ValueText: "nameof" }) return null;
        if (invocation.ArgumentList.Arguments.Count != 1) return null;

        var argument = invocation.ArgumentList.Arguments[0].Expression;
        if (model.GetSymbolInfo(argument, cancellationToken).Symbol is not INamedTypeSymbol { IsUnboundGenericType: false } type)
            return null;
        if (type.TypeArguments.Length == 0) return null;

        var text = argument.ToString();
        var genericStart = text.IndexOf('<');
        if (genericStart <= 0) return null;

        // Only the argument is replaced: the surrounding nameof(...) stays.
        return Match(argument, $"{text[..genericStart]}<>");
    }
}

/// <summary>IDE0029 / IDE0030: <c>x != null ? x : y</c> → <c>x ?? y</c>.</summary>
public sealed class CoalesceExpressionRewrite : CSharpStyleRewrite
{
    private static readonly StyleRule Primary = new(
        "IDE0029", "Null check can be simplified", "x != null ? x : y → x ?? y");

    private static readonly StyleRule Fallback = new(
        "IDE0030", "Null check can be simplified (nullable value types)", "x.HasValue ? x : y → x ?? y");

    public override StyleRule Rule => Primary;

    public override IReadOnlyList<StyleRule> Rules => [Primary, Fallback];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ConditionalExpressionSyntax conditional) return null;

        var (checkedExpression, isNull) = conditional.Condition switch
        {
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.NotEqualsExpression) && IsNullLiteral(binary.Right)
                => (binary.Left, false),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) && IsNullLiteral(binary.Right)
                => (binary.Left, true),
            IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: var nullPattern } } isPattern
                when IsNullLiteral(nullPattern) => (isPattern.Expression, true),
            _ => (null, false),
        };

        if (checkedExpression is null) return null;

        var (tested, fallback) = isNull
            ? (conditional.WhenFalse, conditional.WhenTrue)
            : (conditional.WhenTrue, conditional.WhenFalse);

        if (tested.ToString() != checkedExpression.ToString()) return null;

        return Match(node, $"{checkedExpression} ?? {fallback}");
    }
}

/// <summary>IDE0075: <c>x ? true : false</c> → <c>x</c>, <c>x ? false : true</c> → <c>!x</c>.</summary>
public sealed class SimplifyConditionalRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0075", "Simplify conditional expression", "x ? true : false → x");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ConditionalExpressionSyntax conditional) return null;
        if (conditional.WhenTrue is not LiteralExpressionSyntax whenTrue) return null;
        if (conditional.WhenFalse is not LiteralExpressionSyntax whenFalse) return null;

        var trueValue = whenTrue.IsKind(SyntaxKind.TrueLiteralExpression);
        var falseValue = whenFalse.IsKind(SyntaxKind.TrueLiteralExpression);
        if (trueValue == falseValue) return null;

        var condition = conditional.Condition.ToString();
        if (trueValue) return Match(node, condition);
        return Match(node, conditional.Condition is BinaryExpressionSyntax ? $"!({condition})" : $"!{condition}");
    }
}

/// <summary>IDE0071: <c>$"{x.ToString()}"</c> → <c>$"{x}"</c>.</summary>
public sealed class SimplifyInterpolationRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0071", "Simplify interpolation", "$\"{x.ToString()}\" → $\"{x}\"");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InterpolationSyntax interpolation) return null;
        if (interpolation.AlignmentClause is not null || interpolation.FormatClause is not null) return null;

        if (interpolation.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } call,
                ArgumentList.Arguments.Count: 0,
            }) return null;

        return Match(node, Interpolation(call.Expression));
    }
}
