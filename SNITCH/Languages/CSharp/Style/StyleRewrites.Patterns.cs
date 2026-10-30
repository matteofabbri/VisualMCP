using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

// ── pattern matching ────────────────────────────────────────────────────────
/// <summary>IDE0019: <c>var t = x as T; if (t != null) …</c> → <c>if (x is T t) …</c>.</summary>
public sealed class AsWithNullCheckRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0019", "Use pattern matching to avoid 'as' followed by a null check",
        "var t = x as T; if (t != null) → if (x is T t)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        // Match on the FIRST statement of the shape: `T? t = x as T;` followed by `if (t != null) …`.
        // The service splices the replacement in place of both.
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return null;
        if (declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)) return null;
        if (declaration.Modifiers.Any()) return null;

        var declarator = declaration.Declaration.Variables.SingleOrNone();
        if (declarator is null || declarator.Initializer is null) return null;
        if (declarator.Initializer.Value is not BinaryExpressionSyntax asExpression) return null;
        if (!asExpression.IsKind(SyntaxKind.AsExpression)) return null;

        var ifStatement = node.GetNextStatementViaParent() as IfStatementSyntax;
        if (ifStatement is null || ifStatement.Else is not null) return null;

        var declared = declarator.Identifier.ValueText;
        if (!ConditionTestsVariable(ifStatement.Condition, declared)) return null;

        // The declaration must have exactly the type the `as` produces: `object t = x as string` would
        // silently change what `t` means inside the rewritten block.
        var asType = model.GetTypeInfo(asExpression, cancellationToken).Type;
        if (asType is null) return null;
        if (!declaration.Declaration.Type.IsVar &&
            !SameSymbol(model.GetTypeInfo(declaration.Declaration.Type, cancellationToken).Type, asType))
        {
            return null;
        }

        // The variable's scope shrinks from the block to the if: anything reading it afterwards would
        // be left without a declaration.
        if (StatementNavigation.IsUsedAfter(ifStatement, declared)) return null;

        // The declared name becomes the pattern designation, so the body is untouched: `text` keeps
        // referring to the same (now non-null) value. Only the condition is rebuilt, so the body keeps
        // its own layout (including an Allman brace on the next line).
        var pattern = ParseExpression($"{asExpression.Left} is {asExpression.Right} {declared}");
        var replacement = ifStatement.WithCondition(pattern.WithTriviaFrom(ifStatement.Condition));

        return MatchShape(declaration, ifStatement, replacement.ToString());
    }

    /// <summary>Whether a condition reads exactly "x != null" or "null != x" for the given name.</summary>
    internal static bool ConditionTestsVariable(ExpressionSyntax condition, string name)
    {
        if (condition is IsPatternExpressionSyntax pattern &&
            pattern.Expression is IdentifierNameSyntax patternSubject &&
            patternSubject.Identifier.ValueText == name &&
            pattern.Pattern is UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern } notPattern &&
            notPattern.Pattern is ConstantPatternSyntax constant &&
            constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return true;
        }

        return condition is BinaryExpressionSyntax binary &&
               binary.IsKind(SyntaxKind.NotEqualsExpression) &&
               IsNullAgainstIdentifier(binary, name);
    }

    private static bool IsNullAgainstIdentifier(BinaryExpressionSyntax binary, string name) =>
        (binary.Left is IdentifierNameSyntax left && left.Identifier.ValueText == name && IsNullLiteral(binary.Right)) ||
        (binary.Right is IdentifierNameSyntax right && right.Identifier.ValueText == name && IsNullLiteral(binary.Left));
}

/// <summary>
/// IDE0020 / IDE0038: <c>if (x is T) { … ((T)x) … }</c> → <c>if (x is T t) { … t … }</c>, and the
/// variable-free variant. Only a simple identifier is rewritten: a property or a method call evaluated
/// twice would run twice.
/// </summary>
public sealed class IsWithCastRewrite : CSharpStyleRewrite
{
    private static readonly StyleRule WithVariable = new(
        "IDE0020", "Use pattern matching to avoid 'is' check followed by a cast",
        "if (x is T) { … (T)x … } → if (x is T t) { … t … }");

    private static readonly StyleRule WithoutVariable = new(
        "IDE0038", "Use pattern matching (without a variable)",
        "x is T && ((T)x).P → x is T t && t.P");

    public override StyleRule Rule => WithVariable;

    public override IReadOnlyList<StyleRule> Rules => [WithVariable, WithoutVariable];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax && node is not IfStatementSyntax) return null;

        // && (T)x is the short form: `x is T && (T)x != null` collapses in the condition itself.
        if (node is BinaryExpressionSyntax conjunction && conjunction.IsKind(SyntaxKind.LogicalAndExpression))
            return TryConjunction(conjunction, model, cancellationToken);

        if (node is IfStatementSyntax ifStatement && ifStatement.Else is null)
            return TryIf(ifStatement, model, cancellationToken);

        return null;
    }

    private StyleMatch? TryConjunction(BinaryExpressionSyntax conjunction, SemanticModel model, CancellationToken cancellationToken)
    {
        if (conjunction.Left is not BinaryExpressionSyntax isCheck || !isCheck.IsKind(SyntaxKind.IsExpression)) return null;
        if (isCheck.Left is not IdentifierNameSyntax subjectNode) return null;
        if (isCheck.Right is not TypeSyntax type) return null;

        var castType = model.GetTypeInfo(type, cancellationToken).Type;
        if (castType is null) return null;

        // The subject must be a local or parameter: a property or call would be evaluated twice.
        var symbol = model.GetSymbolInfo(subjectNode, cancellationToken).Symbol;
        if (symbol is not (ILocalSymbol or IParameterSymbol)) return null;

        // The top of the && chain that starts at this is-check; casts to the tested type anywhere in
        // the chain become the pattern variable.
        var chainRoot = (ExpressionSyntax)isCheck;
        while (chainRoot.Parent is BinaryExpressionSyntax parent && parent.IsKind(SyntaxKind.LogicalAndExpression))
            chainRoot = parent;

        // The is-check becomes the declaration first, so the casts are located in the tree that will be
        // published; locating them first and patching by span breaks as soon as a cast shortens the text.
        var name = StatementNavigation.FreshName(chainRoot, "t");
        var withIs = chainRoot.ReplaceNode(isCheck,
            ParseExpression($"{subjectNode} is {type} {name}").WithTriviaFrom(isCheck));

        var casts = withIs.DescendantNodes()
            .OfType<CastExpressionSyntax>()
            .Where(candidate => candidate.Type.ToString() == type.ToString() &&
                                candidate.Expression is IdentifierNameSyntax inner &&
                                inner.Identifier.ValueText == subjectNode.Identifier.ValueText)
            .Select(UnwrapParentheses)
            .ToList();

        if (casts.Count == 0) return null;

        var rewritten = withIs.ReplaceNodes(casts, (original, _) => IdentifierName(name).WithTriviaFrom(original));

        return Match(WithoutVariable, chainRoot, rewritten);
    }

    private StyleMatch? TryIf(IfStatementSyntax ifStatement, SemanticModel model, CancellationToken cancellationToken)
    {
        if (ifStatement.Condition is not BinaryExpressionSyntax isCheck || !isCheck.IsKind(SyntaxKind.IsExpression))
            return null;

        var subject = isCheck.Left as IdentifierNameSyntax;
        var type = isCheck.Right as TypeSyntax;
        if (subject is null || type is null) return null;

        if (ifStatement.Statement is not BlockSyntax block) return null;

        // Every use of `(T)subject` inside the block becomes the pattern variable; the subject must be
        // a local or a parameter so it cannot change between the check and the cast.
        var symbol = model.GetSymbolInfo(subject, cancellationToken).Symbol;
        if (symbol is not (ILocalSymbol or IParameterSymbol)) return null;

        var casts = block.DescendantNodes()
            .OfType<CastExpressionSyntax>()
            .Where(candidate => candidate.Type.ToString() == type.ToString() &&
                                candidate.Expression is IdentifierNameSyntax inner &&
                                inner.Identifier.ValueText == subject.Identifier.ValueText)
            .Select(UnwrapParentheses)
            .ToList();

        if (casts.Count == 0) return null;

        var name = StatementNavigation.FreshName(block, "t");
        var rewritten = block.ReplaceNodes(casts, (original, _) =>
            IdentifierName(name).WithTriviaFrom(original));

        // Only the condition and the body change: rebuilding the whole if from text flattened the
        // original brace placement onto the condition line.
        var pattern = ParseExpression($"{subject} is {type} {name}");
        var replacement = ifStatement
            .WithCondition(pattern.WithTriviaFrom(ifStatement.Condition))
            .WithStatement(rewritten);

        return Match(WithVariable, ifStatement, replacement);
    }

    /// <summary>
    /// The cast, or the parentheses wrapped around it: `((T)x).P` must become `t.P`, not `(t).P`.
    /// A parenthesized cast is always alone inside its parentheses, so replacing the wrapper is exact.
    /// </summary>
    private static SyntaxNode UnwrapParentheses(CastExpressionSyntax cast) =>
        cast.Parent is ParenthesizedExpressionSyntax parentheses ? parentheses : cast;
}

// ── single-statement rewrites ───────────────────────────────────────────────
/// <summary>
/// IDE0016: <c>if (x == null) throw …;</c> followed by a statement that stores x is folded into that
/// store: <c>target = x ?? throw …;</c>. A guard with nothing to fold into has no expression to
/// become, which is why the store is part of the shape.
/// </summary>
public sealed class ThrowExpressionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0016", "Use throw expression",
        "if (x == null) throw …; target = x; → target = x ?? throw …;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not IfStatementSyntax ifStatement || ifStatement.Else is not null) return null;
        if (ifStatement.Statement is not ThrowStatementSyntax { Expression: { } thrown }) return null;

        var guarded = NullChecked(ifStatement.Condition);
        if (guarded is null) return null;

        var next = ifStatement.GetNextStatementViaParent();
        if (next is null) return null;

        // The store the guard protects: `target = x;` or `T local = x;`. A call or a return has no
        // place to put `?? throw` without inventing a temporary.
        var (prefix, value) = next switch
        {
            ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
                when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) =>
                ($"{assignment.Left} = ", assignment.Right),

            LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } local
                when local.Declaration.Variables[0].Initializer is { } initializer =>
                ($"{local.Declaration.Type} {local.Declaration.Variables[0].Identifier} = ", initializer.Value),

            _ => ((string?)null, (ExpressionSyntax?)null),
        };

        // `?? throw` must protect the value the store actually writes, or the guard protects something
        // the assignment never reads.
        if (prefix is null || value is null || value.ToString() != guarded.ToString()) return null;

        return MatchShape(ifStatement, next, $"{prefix}{value} ?? throw {thrown};");
    }

    /// <summary>The expression a condition tests for null, when the whole condition is that check.</summary>
    private static ExpressionSyntax? NullChecked(ExpressionSyntax condition) => condition switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) && IsNullLiteral(binary.Right)
            => binary.Left,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) && IsNullLiteral(binary.Left)
            => binary.Right,
        IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax constant } pattern
            when IsNullLiteral(constant.Expression) => pattern.Expression,
        _ => null,
    };
}
/// <summary>IDE0018: <c>int value; if (int.TryParse(s, out value))</c> → <c>if (int.TryParse(s, out int value))</c>.</summary>
public sealed class InlineVariableDeclarationRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0018", "Inline variable declaration",
        "int value; M(out value) → M(out int value)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        // Match on the FIRST statement: the declaration disappears, the next statement is rewritten.
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (declaration.Modifiers.Any() || declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)) return null;
        if (declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return null;

        var declarator = declaration.Declaration.Variables.SingleOrNone();
        if (declarator is null || declarator.Initializer is not null) return null;

        var name = declarator.Identifier.ValueText;
        var next = node.GetNextStatementViaParent();
        if (next is null) return null;

        var outArguments = next.DescendantNodes()
            .OfType<ArgumentSyntax>()
            .Where(argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            .Where(argument => argument.Expression is IdentifierNameSyntax identifier &&
                               identifier.Identifier.ValueText == name)
            .ToList();

        if (outArguments.Count != 1) return null;

        // Uses after `next` would break: the inline scopes the variable to the statement.
        var root = node.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        var position = root?.Statements.IndexOf(declaration) ?? -1;
        if (root is null || position < 0) return null;
        for (var index = position + 2; index < root.Statements.Count; index++)
        {
            if (root.Statements[index].DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText == name))
            {
                return null;
            }
        }

        var targetType = declaration.Declaration.Type.ToString();
        var rewrittenNext = next.ReplaceNodes(outArguments, (original, _) =>
            Argument(
                null,
                Token(SyntaxKind.OutKeyword).WithTrailingTrivia(Space),
                DeclarationExpression(
                    ParseTypeName(targetType)
                        .WithLeadingTrivia(original.Expression.GetLeadingTrivia())
                        .WithTrailingTrivia(Space),
                    SingleVariableDesignation(
                        Identifier(name).WithTrailingTrivia(original.Expression.GetTrailingTrivia())))));

        return MatchShape(declaration, next, rewrittenNext.ToString());
    }
}

/// <summary>IDE0063: <c>using (var x = …) { … }</c> → <c>using var x = …;</c>.</summary>
public sealed class SimpleUsingRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0063", "Use simple 'using' statement",
        "using (var x = Open()) { body } → using var x = Open(); body");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not UsingStatementSyntax usingStatement) return null;
        if (usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)) return null;
        if (usingStatement.Declaration?.Variables.Count != 1) return null;
        if (usingStatement.Statement is not BlockSyntax block) return null;

        // A body with returns/breaks/continues changes its meaning when the dispose moves to the end
        // of the enclosing block; a straight-line body is the safe case.
        if (block.Statements.Any(statement => statement.DescendantNodesAndSelf().Any(n =>
                n is ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax or
                    YieldStatementSyntax or ThrowStatementSyntax or GotoStatementSyntax)))
        {
            return null;
        }

        var declaration = usingStatement.Declaration!;
        var inner = declaration.Variables[0];

        var usingVariable = LocalDeclarationStatement(
            TokenList(Token(SyntaxKind.UsingKeyword).WithTrailingTrivia(Space)),
            VariableDeclaration(declaration.Type)
                .AddVariables(inner.WithoutTrivia()))
            .NormalizeWhitespace();

        // Statements after it become the enclosing block's; the shape spans just the using statement.
        return MatchShape(node, node, string.Join("\n",
            new[] { usingVariable.ToString() }
                .Concat(block.Statements.Select(statement => statement.ToString().TrimEnd()))));
    }
}

/// <summary>IDE0180: <c>var t = a; a = b; b = t;</c> → <c>(a, b) = (b, a);</c>.</summary>
public sealed class TupleSwapRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0180", "Use tuple to swap values", "var t = a; a = b; b = t; → (a, b) = (b, a);");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalDeclarationStatementSyntax declaration) return null;
        if (declaration.Modifiers.Any()) return null;

        var temp = declaration.Declaration.Variables.SingleOrNone();
        if (temp is null || temp.Initializer is null) return null;

        var name = temp.Identifier.ValueText;

        var first = node.GetNextStatementViaParent() as ExpressionStatementSyntax;
        var second = first is null ? null : first.GetNextStatementViaParent() as ExpressionStatementSyntax;
        if (first?.Expression is not AssignmentExpressionSyntax firstAssignment ||
            second?.Expression is not AssignmentExpressionSyntax secondAssignment)
            return null;

        if (!firstAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ||
            !secondAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            return null;

        // Shape: var temp = a; a = b; b = temp;  →  (a, b) = (b, a);
        var a = temp.Initializer.Value.ToString();
        if (firstAssignment.Left.ToString() == name) return null;          // a = …, not temp = …
        if (secondAssignment.Right.ToString() != name) return null;        // b = temp

        var aText = firstAssignment.Left.ToString();
        var bText = firstAssignment.Right.ToString();
        if (secondAssignment.Left.ToString() != bText) return null;        // b = …
        if (aText == name || bText == name) return null;

        // The temporary must not be used afterwards.
        var root = node.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        var position = root?.Statements.IndexOf(declaration) ?? -1;
        if (root is null || position < 0) return null;
        for (var index = position + 3; index < root.Statements.Count; index++)
        {
            if (root.Statements[index].DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText == name))
            {
                return null;
            }
        }

        // The shape spans all three statements; the swap replaces them.
        return MatchShape(declaration, second!, $"({aText}, {bText}) = ({bText}, {aText});");
    }
}

/// <summary>
/// IDE0260: <c>(o as T)?.M == value</c> → <c>o is T { M: value }</c>. Only a property or field can be a
/// property pattern and only a constant can be its value, so the shape stays narrow — and <c>== null</c>
/// is excluded, because it means "not a T", which the pattern would read the other way round.
/// </summary>
public sealed class ConditionalAccessAsPatternRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0260", "Use pattern matching",
        "(o as string)?.Length == 0 → o is string { Length: 0 }");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax comparison || !comparison.IsKind(SyntaxKind.EqualsExpression))
            return null;

        ConditionalAccessExpressionSyntax? access = null;
        ExpressionSyntax other;
        if (comparison.Left is ConditionalAccessExpressionSyntax onLeft)
        {
            access = onLeft;
            other = comparison.Right;
        }
        else if (comparison.Right is ConditionalAccessExpressionSyntax onRight)
        {
            access = onRight;
            other = comparison.Left;
        }
        else
        {
            return null;
        }

        if (other.IsKind(SyntaxKind.NullLiteralExpression) || other.IsKind(SyntaxKind.DefaultLiteralExpression))
            return null;
        if (!model.GetConstantValue(other, cancellationToken).HasValue) return null;

        if (access.WhenNotNull is not MemberBindingExpressionSyntax { Name: var member }) return null;
        if (model.GetSymbolInfo(member, cancellationToken).Symbol is not (IPropertySymbol or IFieldSymbol))
            return null;

        if (access.Expression is not ParenthesizedExpressionSyntax
            { Expression: BinaryExpressionSyntax asExpression }) return null;
        if (!asExpression.IsKind(SyntaxKind.AsExpression)) return null;

        return Match(node,
            ParseExpression($"{asExpression.Left} is {asExpression.Right} {{ {member}: {other} }}"));
    }
}

public static class StatementNavigation
{
    /// <summary>The only element of a one-element list, or null.</summary>
    public static T? SingleOrNone<T>(this SeparatedSyntaxList<T> list) where T : SyntaxNode =>
        list.Count == 1 ? list[0] : null;

    /// <summary>The statement immediately after this one inside the same block, or null.</summary>
    public static StatementSyntax? GetNextStatementViaParent(this SyntaxNode node) =>
        node.Parent switch
        {
            BlockSyntax block => NextOf(block.Statements, node),
            SwitchSectionSyntax section => NextOf(section.Statements, node),
            _ => null,
        };

    /// <summary>The statement immediately before this one inside the same block, or null.</summary>
    public static StatementSyntax? GetPreviousStatementViaParent(this SyntaxNode node) =>
        node.Parent switch
        {
            BlockSyntax block => PreviousOf(block.Statements, node),
            SwitchSectionSyntax section => PreviousOf(section.Statements, node),
            _ => null,
        };

    private static StatementSyntax? NextOf(SyntaxList<StatementSyntax> statements, SyntaxNode node)
    {
        var index = statements.IndexOf((StatementSyntax)node);
        return index >= 0 && index + 1 < statements.Count ? statements[index + 1] : null;
    }

    private static StatementSyntax? PreviousOf(SyntaxList<StatementSyntax> statements, SyntaxNode node)
    {
        var index = statements.IndexOf((StatementSyntax)node);
        return index > 0 ? statements[index - 1] : null;
    }

    /// <summary>
    /// Whether any statement after <paramref name="statement"/> in its block reads
    /// <paramref name="name"/>. Conservative: a later statement that names it anywhere counts, and a
    /// statement outside a block reports usage, because the scope cannot be reasoned about.
    /// </summary>
    public static bool IsUsedAfter(StatementSyntax statement, string name)
    {
        if (statement.Parent is not BlockSyntax block) return true;

        var index = block.Statements.IndexOf(statement);
        if (index < 0) return true;

        for (var next = index + 1; next < block.Statements.Count; next++)
        {
            if (block.Statements[next].DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText == name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A name that appears nowhere in the enclosing member, so a pattern variable cannot capture (or be
    /// captured by) an existing symbol. A name-based clash is a compile error, and the compiler gate
    /// would then refuse the whole file over one variable.
    /// </summary>
    public static string FreshName(SyntaxNode context, string baseName)
    {
        var taken = context.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                        ?.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Select(identifier => identifier.Identifier.ValueText)
                        .ToHashSet(StringComparer.Ordinal)
                    ?? [];

        var name = baseName;
        for (var suffix = 2; taken.Contains(name); suffix++) name = baseName + suffix;
        return name;
    }
}
