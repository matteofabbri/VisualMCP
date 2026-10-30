using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0035: statements after an unconditional jump can never run. The run stops at a local function —
/// a declaration, not flow — and the rewrite is refused when the range carries a label or a directive,
/// because either could make those statements reachable after all.
/// </summary>
public sealed class UnreachableCodeRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0035", "Remove unreachable code", "return x; Do(); → return x;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not StatementSyntax statement) return null;
        if (statement.Parent is not BlockSyntax block) return null;

        var index = block.Statements.IndexOf(statement);
        if (index <= 0) return null;
        if (!Terminates(block.Statements[index - 1])) return null;

        var last = statement;
        for (var next = index; next < block.Statements.Count; next++)
        {
            var candidate = block.Statements[next];
            if (candidate is LocalFunctionStatementSyntax) break;
            if (candidate is LabeledStatementSyntax) return null;
            if (candidate.DescendantTrivia(descendIntoTrivia: true).Any(IsDirective)) return null;

            last = candidate;
        }

        return Removal(statement, last);
    }

    private static bool Terminates(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax => true,
        ThrowStatementSyntax => true,
        BreakStatementSyntax => true,
        ContinueStatementSyntax => true,
        GotoStatementSyntax => true,
        _ => false,
    };

    private static bool IsDirective(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.IfDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.ElseDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.ElifDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.RegionDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.DefineDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.UndefDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.PragmaChecksumDirectiveTrivia) ||
        trivia.IsKind(SyntaxKind.LineDirectiveTrivia);
}

/// <summary>
/// IDE0051: a private member nothing in the solution references is deleted. This is the first rule whose
/// answer is outside the document — it asks the whole solution through the symbol finder, which is why
/// the engine now hands rules the solution and can delete a node instead of only replacing it. A member
/// carrying an attribute is left alone: reflection and serializers do not show up as references.
/// </summary>
public sealed class UnusedPrivateMemberRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0051", "Remove unused private member", "private int Unused() => 1; → (deleted)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null) return null;

        // The answer comes from the solution, so it is only trustworthy while the tree still is the
        // solution's: after that a symbol from the edited tree has no match in it and everything
        // would look unused.
        if (!context.IsPristine) return null;

        if (node is not MemberDeclarationSyntax member) return null;
        if (member.AttributeLists.Count > 0) return null;

        var symbol = context.Model.GetDeclaredSymbol(member, context.CancellationToken);
        if (symbol is null) return null;
        if (symbol.DeclaredAccessibility != Accessibility.Private || symbol.IsImplicitlyDeclared) return null;
        if (!IsRemovable(symbol)) return null;

        var references = SymbolFinder.FindReferencesAsync(symbol, context.Solution, context.CancellationToken)
            .GetAwaiter().GetResult();

        if (references.SelectMany(reference => reference.Locations).Any(location => !location.IsImplicit))
            return null;

        return Removal(member);
    }

    /// <summary>Only a member that is nobody's override, implementation or entry point can simply go.</summary>
    private static bool IsRemovable(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method =>
            method.MethodKind == MethodKind.Ordinary &&
            !method.IsOverride && !method.IsVirtual && !method.IsAbstract && !method.IsExtern &&
            method.ExplicitInterfaceImplementations.Length == 0 &&
            method.PartialImplementationPart is null && method.PartialDefinitionPart is null,

        IFieldSymbol field => !field.IsConst && !field.IsImplicitlyDeclared,

        IPropertySymbol property =>
            !property.IsIndexer && !property.IsOverride && !property.IsVirtual && !property.IsAbstract &&
            property.ExplicitInterfaceImplementations.Length == 0,

        _ => false,
    };
}
