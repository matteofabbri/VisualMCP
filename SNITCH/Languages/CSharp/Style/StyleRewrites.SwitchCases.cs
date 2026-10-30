using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>Shared by the two rules that complete a switch over an enum.</summary>
internal static class EnumSwitches
{
    /// <summary>The enum a switch is switching on, or null for anything else.</summary>
    public static INamedTypeSymbol? EnumOf(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetTypeInfo(expression, cancellationToken).Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } type
            ? type
            : null;

    /// <summary>
    /// The enum members that no constant label covers, one per distinct value: two members sharing a
    /// value would produce a duplicate case label, which does not compile.
    /// </summary>
    public static List<IFieldSymbol> Missing(INamedTypeSymbol type, HashSet<object> handled)
    {
        var missing = new List<IFieldSymbol>();
        var seen = new HashSet<object>();

        foreach (var member in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (!member.HasConstantValue || member.ConstantValue is null) continue;
            if (handled.Contains(member.ConstantValue)) continue;
            if (!seen.Add(member.ConstantValue)) continue;

            missing.Add(member);
        }

        return missing;
    }

    /// <summary>
    /// A label that compiles where the switch is: the short form when it binds to the member, the
    /// fully qualified one otherwise.
    /// </summary>
    public static string Label(SemanticModel model, SyntaxNode at, INamedTypeSymbol type, IFieldSymbol member, CancellationToken cancellationToken)
    {
        var shortName = $"{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{member.Name}";
        if (Binds(model, at, shortName, member, cancellationToken)) return shortName;

        return $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{member.Name}";
    }

    private static bool Binds(SemanticModel model, SyntaxNode at, string text, ISymbol target, CancellationToken cancellationToken)
    {
        var info = model.GetSpeculativeSymbolInfo(
            at.SpanStart, ParseExpression(text), SpeculativeBindingOption.BindAsExpression);

        return info.Symbol is not null && SymbolEqualityComparer.Default.Equals(info.Symbol, target);
    }
}

/// <summary>
/// IDE0010: a switch over an enum that handles some members but not all gets the rest. Only a switch
/// without a <c>default</c> is touched: there, an added empty section does exactly what "no case
/// matched" did. With a default the new sections would steal those values from it, which is a
/// behaviour change, not a style fix.
/// </summary>
public sealed class MissingSwitchCasesRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0010", "Add missing cases to switch statement",
        "switch (e) { case E.A: … } → adds the enum members nobody handles");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not SwitchStatementSyntax statement || statement.Sections.Count == 0) return null;
        if (statement.Sections.SelectMany(section => section.Labels).Any(label => label is DefaultSwitchLabelSyntax))
            return null;

        if (EnumSwitches.EnumOf(statement.Expression, model, cancellationToken) is not { } type) return null;

        var handled = new HashSet<object>();
        foreach (var label in statement.Sections.SelectMany(section => section.Labels))
        {
            // A pattern or `when` label is not a plain constant: leave the switch alone.
            if (label is not CaseSwitchLabelSyntax constant) return null;

            var value = model.GetConstantValue(constant.Value, cancellationToken);
            if (!value.HasValue || value.Value is null) return null;

            handled.Add(value.Value);
        }

        var missing = EnumSwitches.Missing(type, handled);
        if (missing.Count == 0) return null;

        var sections = statement.Sections;
        foreach (var member in missing)
        {
            var label = EnumSwitches.Label(model, statement, type, member, cancellationToken);
            sections = sections.Add(SwitchSection(
                SingletonList<SwitchLabelSyntax>(CaseSwitchLabel(ParseExpression(label))),
                SingletonList<StatementSyntax>(BreakStatement())));
        }

        return Match(node, statement.WithSections(sections));
    }
}

/// <summary>
/// IDE0072: a switch expression over an enum with a discard arm gets one arm per missing member,
/// carrying the discard's own expression. Equivalent by construction: that value used to reach the
/// discard arm and now reaches an arm holding the same expression.
/// </summary>
public sealed class MissingSwitchArmsRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0072", "Add missing cases to switch expression",
        "e switch { E.A => 1, _ => 0 } → adds E.B => 0 for every missing member");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not SwitchExpressionSyntax expression) return null;

        var discard = expression.Arms.FirstOrDefault(arm => arm.Pattern is DiscardPatternSyntax);
        if (discard is null) return null;

        if (EnumSwitches.EnumOf(expression.GoverningExpression, model, cancellationToken) is not { } type) return null;

        var handled = new HashSet<object>();
        foreach (var arm in expression.Arms)
        {
            // The discard arm is the fallback we copy from, not a handled value.
            if (arm.Pattern is DiscardPatternSyntax) continue;
            if (arm.Pattern is not ConstantPatternSyntax constant) return null;

            var value = model.GetConstantValue(constant.Expression, cancellationToken);
            if (!value.HasValue || value.Value is null) return null;

            handled.Add(value.Value);
        }

        var missing = EnumSwitches.Missing(type, handled);
        if (missing.Count == 0) return null;

        // The new arms go before the discard: anything after it is unreachable (CS8510).
        var discardIndex = expression.Arms.IndexOf(discard);
        var arms = expression.Arms;
        foreach (var member in missing)
        {
            var label = EnumSwitches.Label(model, expression, type, member, cancellationToken);
            arms = arms.Insert(discardIndex++, SwitchExpressionArm(
                ConstantPattern(ParseExpression(label)),
                discard.Expression.WithoutTrivia()));
        }

        return Match(node, expression.WithArms(arms));
    }
}
