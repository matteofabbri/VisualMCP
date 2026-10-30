using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;

namespace Snitch.Languages.VB.Style;

// Every rule here is the Visual Basic shape of an id that Microsoft's table defines for this language.
// The C# matchers did not carry over: VB has no braces, no expression-bodied members, no "?" operators,
// and its null test is the "Is"/"IsNot" operator against the Nothing literal. What carries over is the
// base class, the option bag and the pipeline.

// ── simplification ──────────────────────────────────────────────────────────
/// <summary>IDE0001: a qualified name whose last part already binds on its own → the last part.</summary>
public sealed class SimplifyNameRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0001", "Simplify name", "System.Collections.Generic.List(Of Integer) → List(Of Integer) when the short name binds");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not QualifiedNameSyntax qualified) return null;

        // Only the outermost name: the inner parts are rewritten by the pass that replaces this one.
        if (qualified.Parent is QualifiedNameSyntax) return null;

        // An Imports clause names a namespace, and shortening it would change what it imports. It is not
        // a suggestion the IDE makes either.
        if (qualified.Ancestors().OfType<ImportsStatementSyntax>().Any()) return null;

        var symbol = model.GetSymbolInfo(qualified, cancellationToken).Symbol;
        if (symbol is null) return null;
        if (!BindsTo(model, qualified, qualified.Right.ToString(), symbol, asType: true)) return null;

        return Match(node, qualified.Right.WithTriviaFrom(qualified));
    }
}

/// <summary>IDE0002: <c>Type.SharedMember</c> → <c>SharedMember</c> when the unqualified name binds.</summary>
public sealed class SimplifyMemberAccessRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0002", "Simplify member access", "C.Value → Value when the unqualified name binds");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax memberAccess) return null;
        if (!memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression)) return null;

        // In a conditional access (x?.M) the inner M is a member access with no receiver of its own:
        // the receiver is the conditional access. There is no "Type.Member" to shorten there, and
        // asking the semantic model about a null node throws.
        if (memberAccess.Expression is null) return null;

        if (model.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol is not INamedTypeSymbol) return null;

        var symbol = model.GetSymbolInfo(memberAccess, cancellationToken).Symbol;
        if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or INamedTypeSymbol)) return null;
        if (!BindsTo(model, memberAccess, memberAccess.Name.ToString(), symbol, asType: false)) return null;

        return Match(node, memberAccess.Name.WithTriviaFrom(memberAccess));
    }
}

/// <summary>IDE0003: <c>Me.Member</c> → <c>Member</c> when nothing shadows it.</summary>
public sealed class RemoveMeQualificationRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0003", "Name can be simplified", "Me.Member → Member when the unqualified name binds");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax memberAccess) return null;
        if (!memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression)) return null;
        if (memberAccess.Expression is not MeExpressionSyntax) return null;

        var symbol = model.GetSymbolInfo(memberAccess, cancellationToken).Symbol;
        if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) return null;
        if (!BindsTo(model, memberAccess, memberAccess.Name.ToString(), symbol, asType: false)) return null;

        return Match(node, memberAccess.Name.WithTriviaFrom(memberAccess));
    }
}

/// <summary>IDE0004: a cast to the operand's own type → the operand.</summary>
public sealed class UnnecessaryCastRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0004", "Remove unnecessary cast", "CType(x, Integer) → x when x already has that type");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        // TryCast is excluded on purpose: it answers Nothing instead of throwing, so removing it would
        // change behaviour whenever the cast can fail.
        if (node is not CastExpressionSyntax cast) return null;
        if (!cast.IsKind(SyntaxKind.CTypeExpression) && !cast.IsKind(SyntaxKind.DirectCastExpression)) return null;

        var castType = model.GetTypeInfo(cast, cancellationToken).Type;
        var operandType = model.GetTypeInfo(cast.Expression, cancellationToken).Type;
        if (castType is null || operandType is null) return null;
        if (!SymbolEqualityComparer.Default.Equals(castType, operandType)) return null;

        return Match(node, cast.Expression.WithTriviaFrom(cast));
    }
}

/// <summary>IDE0049: <c>System.Int32</c> → <c>Integer</c>, for a type reference only.</summary>
public sealed class PredefinedTypeRewrite : VbStyleRewrite
{
    private static readonly Dictionary<SpecialType, string> Keywords = new()
    {
        [SpecialType.System_Object] = "Object",
        [SpecialType.System_String] = "String",
        [SpecialType.System_Boolean] = "Boolean",
        [SpecialType.System_Char] = "Char",
        [SpecialType.System_SByte] = "SByte",
        [SpecialType.System_Byte] = "Byte",
        [SpecialType.System_Int16] = "Short",
        [SpecialType.System_UInt16] = "UShort",
        [SpecialType.System_Int32] = "Integer",
        [SpecialType.System_UInt32] = "UInteger",
        [SpecialType.System_Int64] = "Long",
        [SpecialType.System_UInt64] = "ULong",
        [SpecialType.System_Single] = "Single",
        [SpecialType.System_Double] = "Double",
        [SpecialType.System_Decimal] = "Decimal",
    };

    public override StyleRule Rule { get; } = new(
        "IDE0049", "Use language keywords instead of framework type names",
        "System.Int32 → Integer, System.String → String");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        // VB parses a member access as MemberAccessExpressionSyntax, not as a qualified name, so a
        // QualifiedNameSyntax is a type (or namespace) reference: the only place the keyword fits.
        if (node is not QualifiedNameSyntax name) return null;
        if (name.Parent is QualifiedNameSyntax) return null;
        if (name.Ancestors().OfType<ImportsStatementSyntax>().Any()) return null;

        if (model.GetSymbolInfo(name, cancellationToken).Symbol is not INamedTypeSymbol type) return null;
        if (!Keywords.TryGetValue(type.SpecialType, out var keyword)) return null;

        return Match(node, SyntaxFactory.ParseTypeName(keyword).WithTriviaFrom(name));
    }
}

// ── null handling ───────────────────────────────────────────────────────────
/// <summary>IDE0041: <c>x = Nothing</c> → <c>x Is Nothing</c> for a reference comparison only.</summary>
public sealed class IsNothingRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0041", "Use 'Is Nothing' check", "x = Nothing → x Is Nothing, x <> Nothing → x IsNot Nothing");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax binary) return null;
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return null;
        if (IsUserDefinedOperator(model, binary, cancellationToken)) return null;

        var other = IsNothingLiteral(binary.Right) ? binary.Left
            : IsNothingLiteral(binary.Left) ? binary.Right
            : null;

        if (other is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
            or MeExpressionSyntax)) return null;

        // The inner member access of a conditional access has no receiver: there is nothing to compare.
        if (other is MemberAccessExpressionSyntax { Expression: null }) return null;

        // "=" is a value comparison for a value type and a string comparison for String: "Is Nothing" is
        // only equivalent for a reference type that does not redefine equality.
        if (!IsReferenceWithoutValueEquality(model.GetTypeInfo(other, cancellationToken).Type)) return null;

        var isNull = binary.IsKind(SyntaxKind.EqualsExpression);
        return MatchExpression(node, $"{other} {(isNull ? "Is" : "IsNot")} Nothing");
    }
}

/// <summary>IDE0029 / IDE0030: a conditional null check → VB's binary <c>If(a, b)</c> coalesce.</summary>
public sealed class CoalesceRewrite : VbStyleRewrite
{
    private static readonly StyleRule Primary = new(
        "IDE0029", "Null check can be simplified", "If(x IsNot Nothing, x, y) → If(x, y)");

    private static readonly StyleRule Nullable = new(
        "IDE0030", "Null check can be simplified (nullable value types)", "If(x.HasValue, x, y) → If(x, y)");

    public override StyleRule Rule => Primary;

    public override IReadOnlyList<StyleRule> Rules => [Primary, Nullable];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not TernaryConditionalExpressionSyntax conditional) return null;

        if (conditional.Condition is BinaryExpressionSyntax comparison &&
            (comparison.IsKind(SyntaxKind.IsExpression) || comparison.IsKind(SyntaxKind.IsNotExpression)) &&
            IsNothingLiteral(comparison.Right))
        {
            var checkedExpression = comparison.Left;
            var isNull = comparison.IsKind(SyntaxKind.IsExpression);
            var (tested, fallback) = isNull
                ? (conditional.WhenFalse, conditional.WhenTrue)
                : (conditional.WhenTrue, conditional.WhenFalse);

            if (tested.ToString() != checkedExpression.ToString()) return null;
            if (!IsSideEffectFree(checkedExpression)) return null;
            if (!IsReference(model.GetTypeInfo(checkedExpression, cancellationToken).Type)) return null;

            return MatchExpression(Primary, node, $"If({checkedExpression}, {fallback})");
        }

        if (conditional.Condition is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "HasValue" } hasValue)
        {
            var checkedExpression = hasValue.Expression;

            // The inner member access of a conditional access has no receiver of its own.
            if (checkedExpression is null) return null;

            if (conditional.WhenTrue.ToString() != checkedExpression.ToString()) return null;
            if (!IsSideEffectFree(checkedExpression)) return null;

            var type = model.GetTypeInfo(checkedExpression, cancellationToken).Type as INamedTypeSymbol;
            if (type is null || type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T) return null;

            return MatchExpression(Nullable, node, $"If({checkedExpression}, {conditional.WhenFalse})");
        }

        return null;
    }
}

/// <summary>IDE0031: <c>If(x IsNot Nothing, x.M(), Nothing)</c> → <c>x?.M()</c>.</summary>
public sealed class NullPropagationRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0031", "Use null propagation", "If(x IsNot Nothing, x.M(), Nothing) → x?.M()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not TernaryConditionalExpressionSyntax conditional) return null;

        var (receiver, access) = conditional.Condition switch
        {
            BinaryExpressionSyntax { } isNot
                when isNot.IsKind(SyntaxKind.IsNotExpression) && IsNothingLiteral(isNot.Right) &&
                     IsNothingLiteral(conditional.WhenFalse)
                => (isNot.Left, conditional.WhenTrue),
            BinaryExpressionSyntax { } isNull
                when isNull.IsKind(SyntaxKind.IsExpression) && IsNothingLiteral(isNull.Right) &&
                     IsNothingLiteral(conditional.WhenTrue)
                => (isNull.Left, conditional.WhenFalse),
            _ => (null, null),
        };

        if (receiver is null || access is null) return null;
        if (!IsSideEffectFree(receiver)) return null;
        if (!IsReference(model.GetTypeInfo(receiver, cancellationToken).Type)) return null;

        string? text = access switch
        {
            MemberAccessExpressionSyntax member
                when member.Expression is not null && member.Expression.ToString() == receiver.ToString()
                => $"{receiver}?.{member.Name}",
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation
                when member.Expression is not null && member.Expression.ToString() == receiver.ToString()
                => $"{receiver}?.{member.Name}({string.Join(", ", invocation.ArgumentList.Arguments)})",
            _ => null,
        };

        return text is null ? null : MatchExpression(node, text);
    }
}

// ── operators ───────────────────────────────────────────────────────────────
/// <summary>IDE0047: parentheses around a primary expression → nothing.</summary>
public sealed class ParenthesesRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0047", "Remove unnecessary parentheses", "(x).M() → x.M()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ParenthesizedExpressionSyntax parenthesized) return null;

        // Only primary expressions: their precedence never depends on the parentheses.
        if (parenthesized.Expression is not (IdentifierNameSyntax or LiteralExpressionSyntax or InvocationExpressionSyntax
            or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax or MeExpressionSyntax)) return null;

        return Match(node, parenthesized.Expression.WithTriviaFrom(parenthesized));
    }
}

/// <summary>IDE0054: <c>x = x + y</c> → <c>x += y</c>.</summary>
public sealed class CompoundAssignmentRewrite : VbStyleRewrite
{
    /// <summary>
    /// The binary operators VB also has a compound assignment for. There is no compound form of
    /// <c>Mod</c>, <c>And</c>, <c>Or</c> or <c>Xor</c>, so those stay as they are.
    /// </summary>
    private static readonly Dictionary<SyntaxKind, string> Operators = new()
    {
        [SyntaxKind.AddExpression] = "+=",
        [SyntaxKind.SubtractExpression] = "-=",
        [SyntaxKind.MultiplyExpression] = "*=",
        [SyntaxKind.DivideExpression] = "/=",
        [SyntaxKind.IntegerDivideExpression] = "\\=",
        [SyntaxKind.ExponentiateExpression] = "^=",
        [SyntaxKind.ConcatenateExpression] = "&=",
        [SyntaxKind.LeftShiftExpression] = "<<=",
        [SyntaxKind.RightShiftExpression] = ">>=",
    };

    public override StyleRule Rule { get; } = new(
        "IDE0054", "Use compound assignment", "x = x + y → x += y");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not AssignmentStatementSyntax assignment) return null;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)) return null;
        if (assignment.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) return null;
        if (assignment.Right is not BinaryExpressionSyntax binary) return null;
        if (!Operators.TryGetValue(binary.Kind(), out var compound)) return null;
        if (binary.Left.ToString() != assignment.Left.ToString()) return null;

        // A user-defined "+" gives the compound form a different overload to resolve against.
        if (IsUserDefinedOperator(model, binary, cancellationToken)) return null;

        return MatchStatement(node, $"{assignment.Left} {compound} {binary.Right}");
    }
}

/// <summary>IDE0100: <c>x = True</c> → <c>x</c>, <c>x = False</c> → <c>Not x</c>.</summary>
public sealed class BooleanEqualityRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0100", "Remove unnecessary equality operator", "x = True → x, x = False → Not x");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax binary) return null;
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return null;

        var (other, literal) = binary.Left is LiteralExpressionSyntax left && IsBooleanLiteral(left)
            ? (binary.Right, left)
            : binary.Right is LiteralExpressionSyntax right && IsBooleanLiteral(right)
                ? (binary.Left, right)
                : (null, null);

        if (other is null || literal is null) return null;
        if (model.GetTypeInfo(other, cancellationToken).Type?.SpecialType != SpecialType.System_Boolean) return null;

        var negate = (literal.IsKind(SyntaxKind.TrueLiteralExpression) && binary.IsKind(SyntaxKind.NotEqualsExpression)) ||
                     (literal.IsKind(SyntaxKind.FalseLiteralExpression) && binary.IsKind(SyntaxKind.EqualsExpression));

        if (!negate) return MatchExpression(node, other.ToString());

        return MatchExpression(node, other is BinaryExpressionSyntax ? $"Not ({other})" : $"Not {other}");
    }

    private static bool IsBooleanLiteral(LiteralExpressionSyntax literal) =>
        literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression);
}

/// <summary>IDE0075: <c>If(x, True, False)</c> → <c>x</c>, <c>If(x, False, True)</c> → <c>Not x</c>.</summary>
public sealed class SimplifyConditionalRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0075", "Simplify conditional expression", "If(x, True, False) → x");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not TernaryConditionalExpressionSyntax conditional) return null;
        if (conditional.WhenTrue is not LiteralExpressionSyntax whenTrue) return null;
        if (conditional.WhenFalse is not LiteralExpressionSyntax whenFalse) return null;

        var trueValue = whenTrue.IsKind(SyntaxKind.TrueLiteralExpression);
        var falseValue = whenFalse.IsKind(SyntaxKind.TrueLiteralExpression);
        if (trueValue == falseValue) return null;

        if (model.GetTypeInfo(conditional.Condition, cancellationToken).Type?.SpecialType != SpecialType.System_Boolean)
            return null;

        var condition = conditional.Condition.ToString();
        if (trueValue) return MatchExpression(node, condition);

        // A comparison binds tighter than "Not" only when it is the whole operand; parentheses keep the
        // replacement independent of where the conditional expression stood.
        return MatchExpression(node, conditional.Condition is BinaryExpressionSyntax ? $"Not ({condition})" : $"Not {condition}");
    }
}

/// <summary>
/// IDE0084: <c>Not (o Is other)</c> ⇄ <c>o IsNot other</c>, in the direction the repository's
/// <c>visual_basic_style_prefer_isnot_expression</c> asks for.
/// </summary>
public sealed class IsNotRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0084", "Use pattern matching ('IsNot' operator)", "Not (o Is other) → o IsNot other");

    public override string? RequiredOption => "visual_basic_style_prefer_isnot_expression";

    /// <summary>Without the repository's options there is no direction to apply.</summary>
    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preference = context.Options.PreferIsNot;
        if (preference == IsNotPreference.None) return null;

        if (preference == IsNotPreference.PreferIsNot &&
            node is UnaryExpressionSyntax prefix && prefix.IsKind(SyntaxKind.NotExpression))
        {
            // "Not o Is other" and "Not (o Is other)" mean the same thing; the parentheses are optional.
            var operand = prefix.Operand is ParenthesizedExpressionSyntax parenthesized
                ? parenthesized.Expression
                : prefix.Operand;

            if (operand is not BinaryExpressionSyntax comparison || !comparison.IsKind(SyntaxKind.IsExpression))
                return null;

            return MatchExpression(node, $"{comparison.Left} IsNot {comparison.Right}");
        }

        if (preference == IsNotPreference.PreferNot &&
            node is BinaryExpressionSyntax isNot && isNot.IsKind(SyntaxKind.IsNotExpression))
        {
            return MatchExpression(node, $"Not ({isNot.Left} Is {isNot.Right})");
        }

        return null;
    }
}

// ── declarations ────────────────────────────────────────────────────────────
/// <summary>IDE0081: <c>ByVal</c> is the default passing convention → remove it.</summary>
public sealed class RemoveByValRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0081", "Remove 'ByVal'", "Sub F(ByVal value As Integer) → Sub F(value As Integer)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not ParameterSyntax parameter) return null;

        var byVal = parameter.Modifiers.FirstOrDefault(token => token.IsKind(SyntaxKind.ByValKeyword));
        if (byVal.IsKind(SyntaxKind.None)) return null;

        // "ByRef" and "ByVal" cannot be combined, so the parameter keeps its ByRef (or its absence) and
        // only the redundant keyword goes.
        return Match(node, parameter.WithModifiers(parameter.Modifiers.Remove(byVal)));
    }
}
