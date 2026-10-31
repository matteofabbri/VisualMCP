using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;
using static Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Snitch.Languages.VB.Style;

/// <summary>
/// A rule whose answer depends on the repository's options or on the whole solution, so it implements
/// the context entry point; the model-only entry point has nothing to answer and stays empty.
/// </summary>
public abstract class VbContextRewrite : VbStyleRewrite
{
    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;
}

/// <summary>
/// The Visual Basic half of a style rewrite: <see cref="Snitch.Languages.Style.StyleRewrite"/> plus the
/// helpers that need VB's own syntax factory. Visual Basic has no expression-bodied members and no
/// braces, so a rule here never replaces a whole block: it replaces an expression, or one statement.
/// <para>
/// The one factory call worth knowing: VB spells "parse a statement" as
/// <c>SyntaxFactory.ParseExecutableStatement</c> — there is no <c>ParseStatement</c> in this language's
/// factory, and a statement carries its own line-break trivia, which the service then replaces with the
/// trivia of the statement being rewritten.
/// </para>
/// </summary>
public abstract class VbStyleRewrite : Snitch.Languages.Style.StyleRewrite
{
    protected StyleMatch MatchExpression(SyntaxNode node, string expressionText) =>
        Match(node, ParseExpression(expressionText).WithTriviaFrom(node));

    /// <summary>The same, reported under one member of a rule family (IDE0029 versus IDE0030).</summary>
    protected StyleMatch MatchExpression(StyleRule rule, SyntaxNode node, string expressionText) =>
        Match(rule, node, ParseExpression(expressionText).WithTriviaFrom(node));

    protected StyleMatch MatchStatement(SyntaxNode node, string statementText) =>
        Match(node, ParseExecutableStatement(statementText).WithTriviaFrom(node));

    /// <summary>
    /// A member rewritten as a whole: the text is parsed inside a throwaway class, because VB has no
    /// "parse a declaration" entry point — a field, a property block and a method block are members of
    /// a type, not executable statements.
    /// </summary>
    protected StyleMatch MatchMember(SyntaxNode node, string memberText) =>
        Match(node, ParseMember(memberText).WithTriviaFrom(node));

    /// <summary>One member parsed out of a wrapper class; the declaration the text describes.</summary>
    protected static SyntaxNode ParseMember(string text)
    {
        var unit = ParseCompilationUnit(
            "Class __VisualMcpMemberHost" + Environment.NewLine + text + Environment.NewLine + "End Class");

        var host = unit.Members.OfType<TypeBlockSyntax>().FirstOrDefault();
        var member = host?.Members.FirstOrDefault();
        if (member is not null) return member;

        // A malformed fragment still has to answer with a node: the compiler gate refuses the result.
        return ParseExecutableStatement(text);
    }

    /// <summary>
    /// Whether writing <paramref name="nameText"/> at <paramref name="at"/> would bind to
    /// <paramref name="target"/>. VB's speculative binding applies the real scoping rules, so a local or
    /// a parameter that shadows the member makes the answer "no" — a lookup by name alone gets it wrong.
    /// </summary>
    protected static bool BindsTo(SemanticModel model, SyntaxNode at, string nameText, ISymbol? target, bool asType)
    {
        if (target is null) return false;

        // ParseTypeName returns TypeSyntax, which in VB derives from ExpressionSyntax: a type name is an
        // expression here, so both halves feed the same speculative-binding entry point.
        ExpressionSyntax name = asType ? ParseTypeName(nameText) : ParseExpression(nameText);

        var info = model.GetSpeculativeSymbolInfo(
            at.SpanStart,
            name,
            asType ? SpeculativeBindingOption.BindAsTypeOrNamespace : SpeculativeBindingOption.BindAsExpression);

        return SameSymbol(info.Symbol, target);
    }

    /// <summary>True for the <c>Nothing</c> literal, which is VB's only null literal.</summary>
    protected static bool IsNothingLiteral(ExpressionSyntax expression) =>
        expression.IsKind(SyntaxKind.NothingLiteralExpression);

    /// <summary>A type whose <c>Is</c>/<c>IsNot</c> against <c>Nothing</c> is a reference test.</summary>
    protected static bool IsReference(ITypeSymbol? type) => type is { IsReferenceType: true };

    /// <summary>
    /// A reference type for which <c>=</c> is also a reference comparison. <c>String</c> is excluded
    /// because VB compares strings by value: <c>s = Nothing</c> answers true for an empty string, while
    /// <c>s Is Nothing</c> does not. A value type is excluded because <c>= Nothing</c> there means the
    /// default value and <c>Is</c> does not even apply.
    /// </summary>
    protected static bool IsReferenceWithoutValueEquality(ITypeSymbol? type) =>
        IsReference(type) && type!.SpecialType != SpecialType.System_String;

    /// <summary>True when the expression can be written twice without running anything twice.</summary>
    protected static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        MeExpressionSyntax => true,
        LiteralExpressionSyntax => true,
        MemberAccessExpressionSyntax member => IsSideEffectFree(member.Expression),
        _ => false,
    };
}
