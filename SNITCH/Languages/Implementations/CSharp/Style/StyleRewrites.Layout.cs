using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.Style;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0007: with <c>csharp_style_var_*</c> set to true for the declaration's category, an explicit type
/// becomes <c>var</c>. Like every preference rule it does nothing when the repository did not choose.
/// </summary>
public sealed class PreferVarRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0007", "Use 'var'", "Foo x = new Foo(); → var x = new Foo();");

    public override string? RequiredOption => "csharp_style_var_for_built_in_types";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context) =>
        VarRewrite.Plan(node, context, VarPreference.PreferVar, Rule);
}

/// <summary>
/// IDE0008: the opposite direction of the same option — <c>var</c> becomes the explicit type when the
/// category asks for it. A separate rule on purpose: the two directions never run together.
/// </summary>
public sealed class PreferExplicitTypeRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0008", "Use explicit type", "var x = new Foo(); → Foo x = new Foo();");

    public override string? RequiredOption => "csharp_style_var_for_built_in_types";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context) =>
        VarRewrite.Plan(node, context, VarPreference.PreferExplicit, Rule);
}

/// <summary>The shared matcher of IDE0007 and IDE0008: locals and foreach, split by declaration category.</summary>
internal static class VarRewrite
{
    public static StyleMatch? Plan(SyntaxNode node, StyleContext context, VarPreference direction, StyleRule rule)
    {
        switch (node)
        {
            case LocalDeclarationStatementSyntax { Declaration.Variables: { Count: 1 } variables } declaration:
            {
                var variable = variables[0];
                var initializer = variable.Initializer?.Value;
                var declaredType = declaration.Declaration.Type;
                var isVar = IsVar(declaredType);

                // For 'var' the inferred type is the initializer's, with its own nullability annotation:
                // the declared symbol's type can be oblivious and print as 'Foo?'.
                ITypeSymbol? type = (initializer is null ? null : context.Model.GetTypeInfo(initializer, context.CancellationToken).Type)
                    ?? (context.Model.GetDeclaredSymbol(variable, context.CancellationToken) as ILocalSymbol)?.Type;
                if (type is null) return null;
                if (!Wants(context.Options.Var, type, initializer, isVar, direction)) return null;

                if (isVar)
                {
                    var explicitType = Writable(type, context, initializer?.SpanStart ?? declaration.SpanStart);
                    if (explicitType is null) return null;
                    return new StyleMatch(rule, declaration, declaration.WithDeclaration(
                        declaration.Declaration.WithType(ParseTypeName(explicitType).WithTriviaFrom(declaredType))),
                        LineOf(declaration), declaration.ToString(), $"{explicitType} {variable}");
                }

                if (initializer is null) return null; // 'int x;' has no right-hand side to infer from
                if (!Inferable(initializer)) return null;
                return new StyleMatch(rule, declaration, declaration.WithDeclaration(
                    declaration.Declaration.WithType(IdentifierName("var").WithTriviaFrom(declaredType))),
                    LineOf(declaration), declaration.ToString(), $"var {variable}");
            }

            case ForEachStatementSyntax loop:
            {
                var isVar = IsVar(loop.Type);
                var type = context.Model.GetForEachStatementInfo(loop).ElementType;
                if (type is null) return null;
                if (!Wants(context.Options.Var, type, loop.Expression, isVar, direction)) return null;

                if (isVar)
                {
                    var explicitType = Writable(type, context, loop.SpanStart);
                    if (explicitType is null) return null;
                    return new StyleMatch(rule, loop, loop.WithType(ParseTypeName(explicitType).WithTriviaFrom(loop.Type)),
                        LineOf(loop), loop.ToString(), explicitType);
                }

                return new StyleMatch(rule, loop, loop.WithType(IdentifierName("var").WithTriviaFrom(loop.Type)),
                    LineOf(loop), loop.ToString(), "var");
            }

            default:
                return null;
        }
    }

    private static bool IsVar(TypeSyntax type) =>
        type is IdentifierNameSyntax { Identifier.ValueText: "var" };

    /// <summary>
    /// Whether <c>var</c> can take this initializer: an object creation, an array creation, an invocation,
    /// a cast or a plain expression. A collection initializer and a bare <c>null</c>/<c>default</c> cannot
    /// be inferred, and <c>var x = null;</c> would be a compiler error in every file it touched.
    /// </summary>
    private static bool Inferable(ExpressionSyntax initializer) => initializer switch
    {
        InitializerExpressionSyntax => false,
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression) => false,
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.DefaultLiteralExpression) => false,
        _ => true,
    };

    /// <summary>The category option that governs this declaration, applied only in the direction it asks for.</summary>
    private static bool Wants(VarStyle style, ITypeSymbol type, ExpressionSyntax? initializer, bool isVar, VarPreference direction)
    {
        var preference = type.SpecialType != SpecialType.None
            ? style.ForBuiltInTypes
            : IsApparent(initializer) ? style.WhenTypeIsApparent : style.Elsewhere;

        if (preference == VarPreference.None) return false;

        // The option's value decides the direction; the other rule stays silent.
        return isVar ? preference == VarPreference.PreferExplicit && direction == VarPreference.PreferExplicit
                     : preference == VarPreference.PreferVar && direction == VarPreference.PreferVar;
    }

    /// <summary>Whether the initializer spells the type out: <c>new T()</c>, a cast, or <c>as</c>.</summary>
    private static bool IsApparent(ExpressionSyntax? initializer) => initializer switch
    {
        ObjectCreationExpressionSyntax => true,
        CastExpressionSyntax => true,
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } => true,
        _ => false,
    };

    /// <summary>The type as source text, or null when <c>var</c> is unavoidable (anonymous, pointer, dynamic).</summary>
    private static string? Writable(ITypeSymbol type, StyleContext context, int position)
    {
        if (type.IsAnonymousType) return null;
        if (type.TypeKind == TypeKind.Pointer) return null;
        if (type.TypeKind == TypeKind.Dynamic) return null;

        var text = type.ToMinimalDisplayString(context.Model, position);
        return string.IsNullOrWhiteSpace(text) || text == "var" ? null : text;
    }

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}

/// <summary>
/// IDE0048: with <c>dotnet_style_parentheses_in_*</c> set to <c>always_for_clarity</c>, a binary operand
/// whose operator differs from its parent's gets parentheses. Only that value is a rewrite — the
/// <c>never_if_unnecessary</c> side is IDE0047.
/// </summary>
public sealed class ParenthesesForClarityRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0048", "Add parentheses for clarity", "a + b * c → a + (b * c)");

    public override string? RequiredOption => "dotnet_style_parentheses_in_arithmetic_binary_operators";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var style = context.Options.Parentheses;
        if (!style.Any) return null;
        if (node is not BinaryExpressionSyntax child) return null;
        if (child.Parent is not BinaryExpressionSyntax parent) return null;
        if (child.RawKind == parent.RawKind) return null; // same operator: precedence makes it readable

        var kind = (SyntaxKind)child.RawKind;
        var wanted = kind switch
        {
            SyntaxKind.AddExpression or SyntaxKind.SubtractExpression or SyntaxKind.MultiplyExpression
                or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression => style.Arithmetic,
            SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or SyntaxKind.LessThanExpression
                or SyntaxKind.GreaterThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.GreaterThanOrEqualExpression => style.Relational,
            SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.BitwiseAndExpression
                or SyntaxKind.BitwiseOrExpression or SyntaxKind.ExclusiveOrExpression or SyntaxKind.LeftShiftExpression
                or SyntaxKind.RightShiftExpression or SyntaxKind.CoalesceExpression => style.OtherBinary,
            _ => false,
        };

        if (!wanted) return null;

        return Match(node, ParenthesizedExpression(child.WithoutTrivia()).WithTriviaFrom(child));
    }
}

// ── layout: the IDE2000 family, driven by dotnet_style_allow_*_experimental ─────────────────────
/// <summary>Whitespace helpers shared by the layout rules; a comment always stops a collapse.</summary>
internal static class LayoutTrivia
{
    public static bool HasComment(SyntaxTriviaList trivia) => trivia.Any(item =>
        item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        item.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        item.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        item.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
        item.IsKind(SyntaxKind.DisabledTextTrivia));

    public static int EndOfLineCount(SyntaxTriviaList trivia) =>
        trivia.Count(item => item.IsKind(SyntaxKind.EndOfLineTrivia));

    /// <summary>Rebuilds the list keeping at most <paramref name="keep"/> line breaks in any run.</summary>
    public static SyntaxTriviaList Collapse(SyntaxTriviaList trivia, int keep)
    {
        var kept = new List<SyntaxTrivia>(trivia.Count);
        var run = 0;
        foreach (var item in trivia)
        {
            if (item.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                run++;
                if (run <= keep) kept.Add(item);
                continue;
            }

            run = 0;
            kept.Add(item);
        }

        return TriviaList(kept);
    }

    /// <summary>One line break in front of the indentation, using the file's own newline.</summary>
    public static SyntaxTriviaList InsertLineBreak(SyntaxTriviaList leading)
    {
        var newLine = leading.FirstOrDefault(item => item.IsKind(SyntaxKind.EndOfLineTrivia)).ToString();
        var text = string.IsNullOrEmpty(newLine) ? System.Environment.NewLine : newLine;
        return leading.Insert(0, EndOfLine(text));
    }

    /// <summary>The whitespace that starts the line the node is on.</summary>
    public static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia();
        for (var index = leading.Count - 1; index >= 0; index--)
            if (leading[index].IsKind(SyntaxKind.WhitespaceTrivia))
                return leading[index].ToString();

        return string.Empty;
    }

    /// <summary>
    /// The node to replace so an edit inside <paramref name="node"/> survives: a block is itself a
    /// statement, and the engine rebuilds a matched statement's surrounding list from the original
    /// trivia — so the anchor is the closest ancestor that is not a statement.
    /// </summary>
    public static SyntaxNode? AnchorFor(SyntaxNode node)
    {
        var current = node;
        while (current is StatementSyntax)
        {
            current = current.Parent;
            if (current is null) return null;
        }

        return current;
    }

    /// <summary>A match anchored where the engine can keep the trivia: <paramref name="anchor"/> with the node replaced.</summary>
    public static StyleMatch? Anchored(StyleRule rule, SyntaxNode anchor, SyntaxNode node, SyntaxNode replacement)
    {
        var rebuilt = anchor.ReplaceNode(node, replacement);
        var line = anchor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new StyleMatch(rule, anchor, rebuilt, line, anchor.ToString(), rebuilt.ToString());
    }

    /// <summary>The leading trivia of <paramref name="node"/> with its blank lines removed (comments stop it).</summary>
    public static SyntaxTriviaList? WithoutBlankLines(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia();
        if (HasComment(leading) || EndOfLineCount(leading) == 0) return null;
        return Collapse(leading, 0);
    }
}

/// <summary>IDE2000: more than one blank line becomes one when the repository forbids it.</summary>
public sealed class MultipleBlankLinesRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2000", "Remove multiple blank lines", "two blank lines → one");

    public override string? RequiredOption => "dotnet_style_allow_multiple_blank_lines_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowMultipleBlankLines != false) return null;

        switch (node)
        {
            case TypeDeclarationSyntax type when CollapseMembers(type) is { } collapsed:
                return Match(node, collapsed);
            case CompilationUnitSyntax unit when CollapseUnit(unit) is { } collapsed:
                return Match(node, collapsed);
            case BlockSyntax block when CollapseStatements(block) is { } collapsed:
            {
                var anchor = LayoutTrivia.AnchorFor(block);
                return anchor is null ? null : LayoutTrivia.Anchored(Rule, anchor, block, collapsed);
            }
            default:
                return null;
        }
    }

    private static BlockSyntax? CollapseStatements(BlockSyntax block)
    {
        var changed = false;
        var statements = block.Statements.Select(statement =>
        {
            var leading = statement.GetLeadingTrivia();
            if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) < 2) return statement;
            changed = true;
            return statement.WithLeadingTrivia(LayoutTrivia.Collapse(leading, 1));
        }).ToList();

        return changed ? block.WithStatements(List(statements)) : null;
    }

    private static TypeDeclarationSyntax? CollapseMembers(TypeDeclarationSyntax type)
    {
        var changed = false;
        var members = type.Members.Select(member =>
        {
            var leading = member.GetLeadingTrivia();
            if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) < 2) return member;
            changed = true;
            return member.WithLeadingTrivia(LayoutTrivia.Collapse(leading, 1));
        }).ToList();

        return changed ? type.WithMembers(List(members)) : null;
    }

    private static CompilationUnitSyntax? CollapseUnit(CompilationUnitSyntax unit)
    {
        var changed = false;
        var members = unit.Members.Select(member =>
        {
            var leading = member.GetLeadingTrivia();
            if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) < 2) return member;
            changed = true;
            return member.WithLeadingTrivia(LayoutTrivia.Collapse(leading, 1));
        }).ToList();

        return changed ? unit.WithMembers(List(members)) : null;
    }
}

/// <summary>IDE2001: an embedded statement on the same line as its parent moves to its own line.</summary>
public sealed class EmbeddedStatementOnOwnLineRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2001", "Put an embedded statement on its own line", "if (x) Do(); → if (x)\\n    Do();");

    public override string? RequiredOption => "dotnet_style_allow_embedded_statements_on_same_line_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowEmbeddedStatementsOnSameLine != false) return null;
        if (node is not StatementSyntax statement) return null;

        StatementSyntax? embedded = statement switch
        {
            IfStatementSyntax { Statement: var body } => body,
            ForStatementSyntax loop => loop.Statement,
            ForEachStatementSyntax loop => loop.Statement,
            ForEachVariableStatementSyntax loop => loop.Statement,
            WhileStatementSyntax loop => loop.Statement,
            DoStatementSyntax loop => loop.Statement,
            LockStatementSyntax locked => locked.Statement,
            FixedStatementSyntax pinned => pinned.Statement,
            _ => null,
        };

        if (embedded is null or BlockSyntax) return null;

        var leading = embedded.GetLeadingTrivia();
        if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) > 0) return null;

        var indentation = LayoutTrivia.IndentationOf(statement) + "    ";
        var moved = embedded.WithLeadingTrivia(TriviaList(LineBreak(leading), Whitespace(indentation)));

        var elseBody = statement is IfStatementSyntax { Else.Statement: { } alternative } && alternative is not BlockSyntax
            ? alternative
            : null;
        if (elseBody is not null)
        {
            var elseLeading = elseBody.GetLeadingTrivia();
            if (LayoutTrivia.HasComment(elseLeading) || LayoutTrivia.EndOfLineCount(elseLeading) > 0) elseBody = null;
        }

        SyntaxNode? replacement = statement switch
        {
            IfStatementSyntax branch => branch
                .WithStatement(moved)
                .WithElse(elseBody is null
                    ? branch.Else
                    : branch.Else!.WithStatement(elseBody.WithLeadingTrivia(
                        TriviaList(LineBreak(elseBody.GetLeadingTrivia()), Whitespace(indentation))))),
            ForStatementSyntax loop => loop.WithStatement(moved),
            ForEachStatementSyntax loop => loop.WithStatement(moved),
            ForEachVariableStatementSyntax loop => loop.WithStatement(moved),
            WhileStatementSyntax loop => loop.WithStatement(moved),
            DoStatementSyntax loop => loop.WithStatement(moved),
            LockStatementSyntax locked => locked.WithStatement(moved),
            FixedStatementSyntax pinned => pinned.WithStatement(moved),
            _ => null,
        };

        return replacement is null ? null : Match(node, replacement);
    }

    private static SyntaxTrivia LineBreak(SyntaxTriviaList trivia)
    {
        var existing = trivia.FirstOrDefault(item => item.IsKind(SyntaxKind.EndOfLineTrivia)).ToString();
        return EndOfLine(string.IsNullOrEmpty(existing) ? System.Environment.NewLine : existing);
    }
}

/// <summary>IDE2002: no blank line between consecutive braces when the repository forbids it.</summary>
public sealed class BlankLinesBetweenBracesRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2002", "Remove a blank line between consecutive braces", "{ \\n\\n } → { \\n }");

    public override string? RequiredOption => "dotnet_style_allow_blank_lines_between_consecutive_braces_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowBlankLinesBetweenConsecutiveBraces != false) return null;
        if (node is not BlockSyntax block) return null;

        var leading = block.CloseBraceToken.LeadingTrivia;
        if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) == 0) return null;

        var previousIsBrace = block.Statements.Count == 0 ||
                              block.Statements[^1].GetLastToken().IsKind(SyntaxKind.CloseBraceToken);
        if (!previousIsBrace) return null;

        var anchor = LayoutTrivia.AnchorFor(block);
        if (anchor is null) return null;

        return LayoutTrivia.Anchored(Rule, anchor, block, block.WithCloseBraceToken(
            block.CloseBraceToken.WithLeadingTrivia(LayoutTrivia.Collapse(leading, 0))));
    }
}

/// <summary>IDE2003: a statement right after a block gets a blank line when the repository requires it.</summary>
public sealed class BlankLineAfterBlockRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2003", "Add a blank line after a block", "} \\n x(); → } \\n\\n x();");

    public override string? RequiredOption => "dotnet_style_allow_statement_immediately_after_block_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowStatementImmediatelyAfterBlock != false) return null;
        if (node is not BlockSyntax block) return null;

        var changed = false;
        var statements = block.Statements.ToList();

        for (var index = 0; index < statements.Count - 1; index++)
        {
            if (!statements[index].GetLastToken().IsKind(SyntaxKind.CloseBraceToken)) continue;

            var next = statements[index + 1];
            var leading = next.GetLeadingTrivia();
            if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) > 0) continue;

            statements[index + 1] = next.WithLeadingTrivia(LayoutTrivia.InsertLineBreak(leading));
            changed = true;
        }

        return changed ? anchorMatch(block, statements) : null;

        StyleMatch? anchorMatch(BlockSyntax target, List<StatementSyntax> newStatements)
        {
            var anchor = LayoutTrivia.AnchorFor(target);
            if (anchor is null) return null;
            return LayoutTrivia.Anchored(Rule, anchor, target, target.WithStatements(List(newStatements)));
        }
    }
}

/// <summary>IDE2004: no blank line after the colon of a constructor initializer.</summary>
public sealed class ConstructorInitializerBlankLineRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2004", "Remove a blank line after the constructor-initializer colon", ": \\n\\n base(…) → : \\n base(…)");

    public override string? RequiredOption => "dotnet_style_allow_blank_line_after_colon_in_constructor_initializer_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowBlankLineAfterColonInConstructorInitializer != false) return null;
        if (node is not ConstructorInitializerSyntax initializer) return null;

        // The blank line after the colon is a line break in the trivia *before* the this/base keyword:
        // the newline that ends the colon's own line is the colon's trailing trivia.
        var keyword = initializer.ThisOrBaseKeyword;
        var leading = keyword.LeadingTrivia;
        if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) == 0) return null;

        return Match(node, initializer.WithThisOrBaseKeyword(
            keyword.WithLeadingTrivia(LayoutTrivia.Collapse(leading, 0))));
    }
}

/// <summary>IDE2005: no blank line after <c>?</c> or <c>:</c> in a conditional expression.</summary>
public sealed class ConditionalExpressionBlankLineRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2005", "Remove a blank line in a conditional expression", "a ? \\n\\n b : c → a ? \\n b : c");

    public override string? RequiredOption => "dotnet_style_allow_blank_line_after_token_in_conditional_expression_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowBlankLineAfterTokenInConditionalExpression != false) return null;
        if (node is not ConditionalExpressionSyntax conditional) return null;

        // As with the other blank-line rules: the blank line is the leading trivia of the branch, not
        // the trailing trivia of '?' or ':'.
        var whenTrue = conditional.WhenTrue.GetLeadingTrivia();
        var whenFalse = conditional.WhenFalse.GetLeadingTrivia();

        var trueChanged = !LayoutTrivia.HasComment(whenTrue) && LayoutTrivia.EndOfLineCount(whenTrue) > 0;
        var falseChanged = !LayoutTrivia.HasComment(whenFalse) && LayoutTrivia.EndOfLineCount(whenFalse) > 0;
        if (!trueChanged && !falseChanged) return null;

        var replacement = conditional;
        if (trueChanged)
            replacement = replacement.WithWhenTrue(
                replacement.WhenTrue.WithLeadingTrivia(LayoutTrivia.Collapse(whenTrue, 0)));
        if (falseChanged)
            replacement = replacement.WithWhenFalse(
                replacement.WhenFalse.WithLeadingTrivia(LayoutTrivia.Collapse(whenFalse, 0)));

        return Match(node, replacement);
    }
}

/// <summary>IDE2006: no blank line after the <c>=&gt;</c> of an expression-bodied member.</summary>
public sealed class ArrowClauseBlankLineRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2006", "Remove a blank line after '=>'", "=> \\n\\n x → => \\n x");

    public override string? RequiredOption => "dotnet_style_allow_blank_line_after_token_in_arrow_expression_clause_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowBlankLineAfterTokenInArrowExpressionClause != false) return null;
        if (node is not ArrowExpressionClauseSyntax clause) return null;

        var leading = clause.Expression.GetLeadingTrivia();
        if (LayoutTrivia.HasComment(leading) || LayoutTrivia.EndOfLineCount(leading) == 0) return null;

        return Match(node, clause.WithExpression(
            clause.Expression.WithLeadingTrivia(LayoutTrivia.Collapse(leading, 0))));
    }
}
