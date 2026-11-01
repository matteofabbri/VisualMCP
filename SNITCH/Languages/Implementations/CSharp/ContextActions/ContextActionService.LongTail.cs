using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The second half of the context-action catalogue: the statement and declaration shapes ReSharper puts
/// in the Alt+Enter menu next to the style rewrites — merging and splitting conditions, joining a null
/// check with its use, <c>var</c> and back, splitting a declaration, removing braces, enabling nullable
/// reference types in a file, digit separators and <c>ConfigureAwait(false)</c>. Each is one verified
/// splice over the document, compiler-gated like the first half.
/// </summary>
public sealed partial class ContextActionService
{
    // ── conditions ─────────────────────────────────────────────────────────────

    /// <summary>'if (a) { if (b) body }' → 'if (a &amp;&amp; b) body'.</summary>
    private static Outcome MergeSequentialChecks(Target target)
    {
        var outer = target.Root.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(node => LineOf(node) <= target.Line && EndLine(node) >= target.Line)
            .OrderBy(node => node.SpanStart)
            .FirstOrDefault(IsMergeable);

        if (outer is null)
            return Outcome.Fail(
                "No outer 'if' with a block holding exactly one 'if' (neither with an else) was found on that line.");

        var inner = (IfStatementSyntax)((BlockSyntax)outer.Statement).Statements[0];

        var condition = SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            Parenthesized(outer.Condition),
            Parenthesized(inner.Condition));

        var merged = outer
            .WithCondition(condition)
            .WithStatement(inner.Statement.WithoutTrivia())
            .WithElse(null)
            .WithoutTrivia();

        return new Outcome(
            ReplaceSpan(target, outer.Span, Snippet(merged, IndentationAt(target.Text, LineOf(outer)), NewLineOf(target.Text.ToString()))),
            [$"merged '{inner.Condition}' into the outer condition"]);
    }

    private static bool IsMergeable(IfStatementSyntax outer) =>
        outer.Else is null &&
        outer.Statement is BlockSyntax { Statements.Count: 1 } block &&
        block.Statements[0] is IfStatementSyntax { Else: null };

    /// <summary>'if (a &amp;&amp; b) body' → 'if (a) { if (b) body }'.</summary>
    private static Outcome SplitAndInIf(Target target)
    {
        var outer = target.Root.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(node => LineOf(node) <= target.Line && EndLine(node) >= target.Line)
            .OrderBy(node => node.SpanStart)
            .FirstOrDefault(node =>
                node.Else is null &&
                node.Condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression });

        if (outer is null)
            return Outcome.Fail("No 'if' with a '&&' condition and no else was found on that line.");

        var and = (BinaryExpressionSyntax)outer.Condition;
        var innerIf = SyntaxFactory.IfStatement(and.Right.WithoutTrivia(), outer.Statement.WithoutTrivia());
        var nested = SyntaxFactory.IfStatement(and.Left.WithoutTrivia(), SyntaxFactory.Block(innerIf)).WithoutTrivia();

        return new Outcome(
            ReplaceSpan(target, outer.Span, Snippet(nested, IndentationAt(target.Text, LineOf(outer)), NewLineOf(target.Text.ToString()))),
            ["split the '&&' condition into nested ifs"]);
    }

    /// <summary>'if (x != null) { x.M(); }' → 'x?.M();'.</summary>
    private static Outcome JoinNullCheckWithUsage(Target target)
    {
        var statement = target.Root.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(node => LineOf(node) == target.Line || (LineOf(node) <= target.Line && EndLine(node) >= target.Line))
            .OrderByDescending(LineOf)
            .FirstOrDefault(node => node.Else is null && TryNullCheckedReceiver(node.Condition, out _) &&
                                    SingleStatement(node.Statement) is not null);

        if (statement is null)
            return Outcome.Fail(
                "No 'if (x != null) { one statement }' was found on that line (the body must be a single statement and there is no else).");

        TryNullCheckedReceiver(statement.Condition, out var receiver);
        var body = SingleStatement(statement.Statement)!;
        var receiverText = receiver!.ToString();
        var bodyText = body.ToString().TrimStart();

        // The body must *be* a use of the receiver; rewriting an occurrence inside an argument would
        // drop the null check while the compiler still accepted the result.
        if (!bodyText.StartsWith(receiverText + ".", StringComparison.Ordinal))
            return Outcome.Fail(
                $"The body does not start a member access on '{receiverText}', so a conditional access would not replace the check.");

        var rewritten = receiverText + "?." + bodyText[(receiverText.Length + 1)..];
        var indentation = IndentationAt(target.Text, LineOf(statement));
        var replacement = string.Join(NewLineOf(target.Text.ToString()),
            rewritten.Replace("\r\n", "\n").Split('\n').Select((line, index) => index == 0 ? indentation + line.Trim() : line));

        return new Outcome(ReplaceSpan(target, statement.Span, replacement),
            [$"replaced the null check with '{receiverText}?.'"]);
    }

    private static bool TryNullCheckedReceiver(ExpressionSyntax condition, out ExpressionSyntax? receiver)
    {
        receiver = null;
        if (condition is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } binary) return false;

        if (IsNull(binary.Right)) receiver = binary.Left;
        else if (IsNull(binary.Left)) receiver = binary.Right;
        return receiver is not null;
    }

    private static bool IsNull(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression };

    private static StatementSyntax? SingleStatement(StatementSyntax statement) => statement switch
    {
        BlockSyntax { Statements.Count: 1 } block => block.Statements[0],
        BlockSyntax => null,
        _ => statement,
    };

    // ── declarations ───────────────────────────────────────────────────────────

    /// <summary>The explicit type of a local declaration becomes <c>var</c>.</summary>
    private static Outcome UseVar(Target target)
    {
        var declaration = DeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail("No local declaration on that line.");
        if (declaration.Declaration.Type.IsVar) return Outcome.Fail("The declaration already uses 'var'.");
        if (declaration.Declaration.Variables.Count != 1)
            return Outcome.Fail("'var' cannot be used with several declarators; split the declaration first (split_declaration).");
        if (declaration.Declaration.Variables[0].Initializer is null)
            return Outcome.Fail("The declaration has no initializer, so 'var' cannot infer a type.");

        return new Outcome(
            ReplaceSpan(target, declaration.Declaration.Type.Span, "var"),
            [$"'{declaration.Declaration.Type}' → var"]);
    }

    /// <summary>A <c>var</c> local declaration gets the type the compiler inferred.</summary>
    private static Outcome UseExplicitType(Target target)
    {
        var declaration = DeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail("No local declaration on that line.");
        if (!declaration.Declaration.Type.IsVar) return Outcome.Fail("The declaration does not use 'var'.");

        var initializer = declaration.Declaration.Variables[0].Initializer?.Value;
        if (initializer is null) return Outcome.Fail("The declaration has no initializer.");

        var type = target.Model.GetTypeInfo(initializer).Type;
        if (type is null || type.TypeKind == TypeKind.Error)
            return Outcome.Fail("The compiler could not infer the type of the initializer.");
        if (type.IsAnonymousType)
            return Outcome.Fail("The initializer has an anonymous type, which has no name to write.");

        var name = type.ToMinimalDisplayString(target.Model, initializer.SpanStart);
        return new Outcome(
            ReplaceSpan(target, declaration.Declaration.Type.Span, name),
            [$"var → {name}"]);
    }

    /// <summary>'int a = 1, b = 2;' → one declaration per line.</summary>
    private static Outcome SplitDeclaration(Target target)
    {
        var declaration = DeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail("No local declaration on that line.");
        if (declaration.Declaration.Variables.Count < 2)
            return Outcome.Fail("The declaration has one declarator; there is nothing to split.");
        if (declaration.UsingKeyword.RawKind != 0)
            return Outcome.Fail("A 'using' declaration is refused: splitting it would duplicate the disposal scope.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(declaration));
        var modifiers = declaration.Modifiers.Count > 0
            ? string.Join(" ", declaration.Modifiers.Select(token => token.Text)) + " "
            : string.Empty;
        var type = declaration.Declaration.Type.ToString();

        var lines = declaration.Declaration.Variables
            .Select(variable => $"{indentation}{modifiers}{type} {variable};")
            .ToList();

        return new Outcome(ReplaceSpan(target, declaration.Span, string.Join(newLine, lines)),
            [$"split into {lines.Count} declarations"]);
    }

    private static LocalDeclarationStatementSyntax? DeclarationOnLine(Target target) =>
        target.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(node => LineOf(node) == target.Line)
            .OrderBy(node => node.SpanStart)
            .FirstOrDefault();

    /// <summary>'if (x) { OneStatement(); }' → 'if (x) OneStatement();'</summary>
    private static Outcome RemoveBraces(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<BlockSyntax>()
            .FirstOrDefault(node => LineOf(node) == target.Line);

        if (block is null) return Outcome.Fail("No block opens on that line.");

        if (block.Parent is not (IfStatementSyntax or ElseClauseSyntax or ForStatementSyntax or ForEachStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax or UsingStatementSyntax or LockStatementSyntax))
            return Outcome.Fail("The block is not the embedded statement of a control statement.");

        if (block.Statements.Count != 1)
            return Outcome.Fail($"The block holds {block.Statements.Count} statements; removing the braces would change the scope.");

        var statement = block.Statements[0];
        if (statement is IfStatementSyntax or LocalDeclarationStatementSyntax or LabeledStatementSyntax)
            return Outcome.Fail($"The single statement is a {statement.Kind()}; removing the braces would change what it binds to.");

        var indentation = IndentationAt(target.Text, LineOf(block));
        var newLine = NewLineOf(target.Text.ToString());
        return new Outcome(
            ReplaceSpan(target, block.Span, Snippet(statement, indentation, newLine)),
            ["removed the braces"]);
    }

    // ── file-level / token-level ───────────────────────────────────────────────

    /// <summary>Ensures <c>#nullable enable</c> in the file (turning a disable into it when one exists).</summary>
    private static Outcome MigrateToNullable(Target target)
    {
        var source = target.Text.ToString();
        var newLine = NewLineOf(source);

        for (var index = 0; index < target.Text.Lines.Count; index++)
        {
            var line = target.Text.Lines[index];
            var lineText = line.ToString();
            var trimmed = lineText.TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '\uFEFF') trimmed = trimmed[1..].TrimStart();
            if (!trimmed.StartsWith("#nullable", StringComparison.Ordinal)) continue;

            if (trimmed.Contains("enable", StringComparison.OrdinalIgnoreCase))
                return Outcome.Fail("The file already enables nullable reference types.");

            var indentation = lineText[..(lineText.Length - trimmed.Length)];
            return new Outcome(
                ReplaceSpan(target, line.Span, indentation + "#nullable enable"),
                ["replaced the existing #nullable directive"]);
        }

        return new Outcome("#nullable enable" + newLine + source, ["inserted #nullable enable at the top of the file"]);
    }

    /// <summary>Removes the '_' separators from the numeric literal on the line.</summary>
    private static Outcome RemoveDigitSeparators(Target target)
    {
        var literal = target.Root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(node => LineOf(node) == target.Line &&
                           node.IsKind(SyntaxKind.NumericLiteralExpression) &&
                           node.Token.Text.Contains('_'))
            .OrderBy(node => node.SpanStart)
            .FirstOrDefault();

        if (literal is null) return Outcome.Fail("No numeric literal with digit separators on that line.");

        var compact = literal.Token.Text.Replace("_", string.Empty, StringComparison.Ordinal);
        return new Outcome(
            ReplaceSpan(target, literal.Token.Span, compact),
            [$"'{literal.Token.Text}' → '{compact}'"]);
    }

    /// <summary>'await CallAsync()' → 'await CallAsync().ConfigureAwait(false)'.</summary>
    private static Outcome UseConfigureAwaitFalse(Target target)
    {
        var awaitExpression = target.Root.DescendantNodes().OfType<AwaitExpressionSyntax>()
            .Where(node => LineOf(node) == target.Line || (LineOf(node) <= target.Line && EndLine(node) >= target.Line))
            .OrderByDescending(LineOf)
            .FirstOrDefault();

        if (awaitExpression is null) return Outcome.Fail("No 'await' expression on that line.");
        if (awaitExpression.Expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" }
            })
            return Outcome.Fail("The await already calls ConfigureAwait.");

        var operand = awaitExpression.Expression;
        var text = operand.ToString();
        var needsParentheses = operand is BinaryExpressionSyntax or ConditionalExpressionSyntax or AssignmentExpressionSyntax
            or LambdaExpressionSyntax or QueryExpressionSyntax;
        var replacement = (needsParentheses ? "(" + text + ")" : text) + ".ConfigureAwait(false)";

        return new Outcome(
            ReplaceSpan(target, operand.Span, replacement),
            ["added ConfigureAwait(false)"]);
    }

    // ── shared helpers ─────────────────────────────────────────────────────────

    private static ExpressionSyntax Parenthesized(ExpressionSyntax expression) =>
        expression is BinaryExpressionSyntax or ConditionalExpressionSyntax or AssignmentExpressionSyntax
            or LambdaExpressionSyntax or QueryExpressionSyntax
            ? SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia())
            : expression.WithoutTrivia();

    private static string ReplaceSpan(Target target, TextSpan span, string replacement)
    {
        var source = target.Text.ToString();
        return source[..span.Start] + replacement + source[span.End..];
    }

    /// <summary>A node normalised with 4-space indentation, then shifted to the target line's indentation.</summary>
    private static string Snippet(SyntaxNode node, string indentation, string newLine)
    {
        var text = node.NormalizeWhitespace().ToFullString().Replace("\r\n", "\n").TrimEnd();
        var lines = text.Split('\n');
        return string.Join(newLine, lines.Select((line, index) =>
            index == 0 ? indentation + line : line.Length == 0 ? line : indentation + line));
    }
}
