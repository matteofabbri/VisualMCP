using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;

namespace Snitch.Languages.VB.Style;

// The expression-shaped VB rules: each replaces one expression with another whose value is the same,
// so the compiler gate is a formality and the interesting part is the shape it refuses to touch.

/// <summary>IDE0082: <c>GetType(T).Name</c> → <c>NameOf(T)</c>.</summary>
public sealed class TypeOfNameRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0082", "Convert 'typeof(...).Name' to 'nameof(...)'",
        "GetType(T).Name → NameOf(T): the same string, resolved at compile time");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax member) return null;
        if (!member.IsKind(SyntaxKind.SimpleMemberAccessExpression)) return null;
        if (member.Expression is not GetTypeExpressionSyntax getType) return null;
        if (!string.Equals(member.Name.Identifier.ValueText, "Name", StringComparison.Ordinal)) return null;

        return MatchExpression(node, "NameOf(" + getType.Type + ")");
    }
}

/// <summary>
/// IDE0280: the string literal a <c>paramName</c> parameter receives becomes <c>NameOf(...)</c>. Only
/// the argument list of the exception constructors whose parameter is named <c>paramName</c> is touched:
/// a literal that merely looks like a name is not a name.
/// </summary>
public sealed class ArgumentNameOfRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0280", "Use nameof", "New ArgumentNullException(\"value\") → New ArgumentNullException(NameOf(value))");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LiteralExpressionSyntax literal) return null;
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) return null;

        var value = literal.Token.ValueText;
        if (value.Length == 0 || value.Contains(' ', StringComparison.Ordinal) || value.Contains('.', StringComparison.Ordinal))
            return null;

        var argument = literal.FirstAncestorOrSelf<SimpleArgumentSyntax>();
        if (argument is null || argument.Expression != literal) return null;
        if (argument.NameColonEquals is not null) return null;

        var argumentList = argument.Parent as ArgumentListSyntax;
        var call = argumentList?.Parent;
        if (call is not (ObjectCreationExpressionSyntax or InvocationExpressionSyntax)) return null;

        if (model.GetSymbolInfo(call, cancellationToken).Symbol is not IMethodSymbol method) return null;

        var index = argumentList!.Arguments.IndexOf(argument);
        if (index < 0 || index >= method.Parameters.Length) return null;
        if (!string.Equals(method.Parameters[index].Name, "paramName", StringComparison.OrdinalIgnoreCase)) return null;

        // The name has to be something the call site can see, or NameOf would not compile there.
        var visible = model.LookupSymbols(literal.SpanStart, name: value).Any(symbol =>
            symbol is IParameterSymbol or ILocalSymbol or IFieldSymbol or IPropertySymbol);
        if (!visible) return null;

        return MatchExpression(node, "NameOf(" + value + ")");
    }
}

/// <summary>IDE0071: an interpolation hole that only calls <c>ToString()</c> is the hole itself.</summary>
public sealed class SimplifyInterpolationRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0071", "Simplify interpolation",
        "'${\"\"}' keeps only the receiver: {x.ToString()} → {x}, and {x.ToString():N2} → {x:N2}");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InterpolationSyntax interpolation) return null;
        if (interpolation.Expression is not InvocationExpressionSyntax invocation) return null;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return null;
        if (!member.IsKind(SyntaxKind.SimpleMemberAccessExpression)) return null;
        if (!string.Equals(member.Name.Identifier.ValueText, "ToString", StringComparison.Ordinal)) return null;
        if (invocation.ArgumentList.Arguments.Count != 0) return null;

        // The receiver has to be side-effect free to be written in the same place at the same time.
        if (!IsSideEffectFree(member.Expression)) return null;

        return Match(node, interpolation.WithExpression(member.Expression).WithTriviaFrom(interpolation));
    }
}

/// <summary>
/// IDE0033: <c>t.Item1</c> → <c>t.Id</c> when the tuple element carries a name. A tuple whose elements
/// are all <c>ItemN</c> has nothing to offer, and a receiver that is not a tuple is not this rule's.
/// </summary>
public sealed class TupleElementNameRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0033", "Provide a tuple element name",
        "t.Item1 → t.Id when the tuple declares the element as Id");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax member) return null;
        if (!member.IsKind(SyntaxKind.SimpleMemberAccessExpression)) return null;

        var name = member.Name.Identifier.ValueText;
        if (!name.StartsWith("Item", StringComparison.Ordinal) ||
            !int.TryParse(name.AsSpan(4), out var ordinal) || ordinal < 1)
            return null;

        if (model.GetTypeInfo(member.Expression, cancellationToken).Type is not INamedTypeSymbol { IsTupleType: true } tuple)
            return null;
        if (tuple.TupleElements.Length < ordinal) return null;

        var element = tuple.TupleElements[ordinal - 1].Name;
        if (string.IsNullOrEmpty(element) ||
            string.Equals(element, name, StringComparison.Ordinal) ||
            element.StartsWith("Item", StringComparison.Ordinal) && int.TryParse(element.AsSpan(4), out _))
            return null;
        if (element == "Rest") return null;

        return MatchExpression(node, member.Expression.ToString() + "." + element);
    }
}

/// <summary>
/// IDE0140: <c>Dim x As T = New T(...)</c> → <c>Dim x As New T(...)</c>. The declaration already says
/// the type twice; VB lets it say it once.
/// </summary>
public sealed class SimplifyObjectCreationRewrite : VbStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0140", "Simplify object creation",
        "Dim x As C = New C() → Dim x As New C()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (declaration.Declarators.Count != 1) return null;

        var declarator = declaration.Declarators[0];
        if (declarator.Names.Count != 1) return null;
        if (declarator.AsClause is not SimpleAsClauseSyntax asClause) return null;
        if (declarator.Initializer?.Value is not ObjectCreationExpressionSyntax creation) return null;
        if (creation.Initializer is not null) return null;

        var declared = model.GetTypeInfo(asClause.Type, cancellationToken).Type;
        var created = model.GetTypeInfo(creation, cancellationToken).Type;
        if (declared is null || created is null || !SymbolEqualityComparer.Default.Equals(declared, created)) return null;

        var text = declaration.ToString();
        var typeStart = asClause.Type.SpanStart - declaration.SpanStart;
        var creationStart = creation.SpanStart - declaration.SpanStart;

        // Keep everything before the "As", then let the creation speak for the type.
        var head = text[..typeStart];
        var creationText = text[creationStart..];

        return MatchStatement(node, head + creationText);
    }
}

/// <summary>
/// IDE0048: parentheses around the left operand of a same-family binary operator, which is what
/// <c>always_for_clarity</c> asks for. Off by default and opt-in: IDE0047 removes the same parentheses,
/// so the two can only ever be a team decision.
/// </summary>
public sealed class ParenthesesForClarityRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0048", "Add parentheses for clarity",
        "a + b + c → (a + b) + c when the repository asks for them");

    public override string? RequiredOption => "dotnet_style_parentheses_in_*";

    public override bool IsOptIn => true;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not BinaryExpressionSyntax binary) return null;
        var options = context.Options.Parentheses;
        if (!options.Any) return null;

        if (binary.Left is not BinaryExpressionSyntax left) return null;
        if (left.Left is ParenthesizedExpressionSyntax || left.Parent is ParenthesizedExpressionSyntax) return null;

        var family = Family(binary);
        if (family == OperatorFamily.None || family != Family(left)) return null;
        if (!Enabled(options, family)) return null;

        return Match(node, binary.WithLeft(SyntaxFactory.ParseExpression("(" + left + ")").WithTriviaFrom(left)));
    }

    private enum OperatorFamily
    {
        None,
        Arithmetic,
        Relational,
        OtherBinary,
    }

    private static OperatorFamily Family(BinaryExpressionSyntax binary) => binary.Kind() switch
    {
        SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression
            or SyntaxKind.DivideExpression or SyntaxKind.IntegerDivideExpression or SyntaxKind.ModuloExpression
            or SyntaxKind.ExponentiateExpression => OperatorFamily.Arithmetic,
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression
            or SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanExpression
            or SyntaxKind.GreaterThanOrEqualExpression => OperatorFamily.Relational,
        SyntaxKind.AndAlsoExpression or SyntaxKind.OrElseExpression or SyntaxKind.AndExpression
            or SyntaxKind.OrExpression or SyntaxKind.ExclusiveOrExpression => OperatorFamily.OtherBinary,
        _ => OperatorFamily.None,
    };

    private static bool Enabled(ParenthesesStyle options, OperatorFamily family) => family switch
    {
        OperatorFamily.Arithmetic => options.Arithmetic,
        OperatorFamily.Relational => options.Relational,
        _ => options.OtherBinary,
    };
}

/// <summary>
/// IDE0009: the qualified form of an instance member — <c>Me.X</c> — when the repository asks for it.
/// Off unless <c>dotnet_style_qualification_for_*</c> says true, and opt-in: IDE0003 removes the same
/// qualifier, so "all" cannot run both.
/// </summary>
public sealed class QualifyMemberAccessRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0009", "Member access should be qualified",
        "X → Me.X for an instance field, property, method or event");

    public override string? RequiredOption => "dotnet_style_qualification_for_*";

    public override bool IsOptIn => true;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not IdentifierNameSyntax identifier) return null;
        var qualification = context.Options.Qualification;
        if (!qualification.Any) return null;

        // The name of a member access is already qualified by whatever is on its left; the shorthand
        // ".Name" inside a With block has no receiver at all and Me there would change the target.
        if (identifier.Parent is MemberAccessExpressionSyntax parentAccess && parentAccess.Name == identifier) return null;
        if (identifier.Parent is QualifiedNameSyntax) return null;
        if (identifier.Ancestors().Any(node => node.IsKind(SyntaxKind.NameOfExpression))) return null;
        if (identifier.Parent is LabelStatementSyntax) return null;

        var symbol = context.Model.GetSymbolInfo(identifier, context.CancellationToken).Symbol;
        if (symbol is null || symbol.IsStatic) return null;
        if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) return null;
        if (symbol.ContainingType is null) return null;

        var preference = symbol switch
        {
            IFieldSymbol => qualification.Field,
            IPropertySymbol => qualification.Property,
            IMethodSymbol => qualification.Method,
            IEventSymbol => qualification.Event,
            _ => QualificationPreference.None,
        };
        if (preference != QualificationPreference.PreferQualified) return null;

        // Me has to be legal here: not in a Shared member, not in an attribute, and the qualified name
        // must still bind to the same symbol.
        if (!BindsTo(context.Model, identifier, "Me." + identifier.Identifier.ValueText, symbol, asType: false)) return null;

        return MatchExpression(node, "Me." + identifier.Identifier.ValueText);
    }
}
