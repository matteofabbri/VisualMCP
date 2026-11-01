using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.ContextActions;

public sealed partial class VbContextActionService
{
    /// <summary><c>If a Then</c> / <c>If b Then</c> / body → <c>If (a) AndAlso (b) Then</c> / body.</summary>
    private Outcome MergeSequentialChecks(Target target)
    {
        var outer = target.Root.DescendantNodes().OfType<MultiLineIfBlockSyntax>()
            .Where(block => LineOf(block) <= target.Line && EndLine(block) >= target.Line)
            .OrderBy(block => block.SpanStart)
            .FirstOrDefault(block => block.ElseIfBlocks.Count == 0 && block.ElseBlock is null &&
                                     block.Statements.Count == 1 &&
                                     block.Statements[0] is MultiLineIfBlockSyntax { ElseIfBlocks.Count: 0, ElseBlock: null });

        if (outer is null)
            return Outcome.Fail(
                "No outer If whose body holds exactly one If (neither with an ElseIf or Else) was found on that line.");

        var inner = (MultiLineIfBlockSyntax)outer.Statements[0];
        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(outer));
        var body = TrimRegion(original.Substring(inner.IfStatement.Span.End,
            inner.EndIfStatement.SpanStart - inner.IfStatement.Span.End));
        var innerIndent = indentation + "        ";
        var bodyIndent = indentation + "    ";
        var reindented = body.Length == 0 ? string.Empty : Reindent(body, innerIndent, bodyIndent, newLine);

        var replacement = "If (" + outer.IfStatement.Condition + ") AndAlso (" + inner.IfStatement.Condition + ") Then" +
                          newLine +
                          (reindented.Length == 0 ? string.Empty : reindented + newLine) +
                          indentation + "End If";

        return new Outcome(ReplaceNode(target, outer, replacement),
            [$"merged '{inner.IfStatement.Condition}' into the outer condition"]);
    }

    /// <summary><c>If a AndAlso b Then</c> body → nested <c>If a Then</c> / <c>If b Then</c> body.</summary>
    private Outcome SplitAndInIf(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<MultiLineIfBlockSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderBy(candidate => candidate.SpanStart)
            .FirstOrDefault(candidate => candidate.ElseIfBlocks.Count == 0 && candidate.ElseBlock is null &&
                                         candidate.IfStatement.Condition is BinaryExpressionSyntax condition &&
                                         condition.IsKind(SyntaxKind.AndAlsoExpression));

        if (block is null)
            return Outcome.Fail("No If with an AndAlso condition and no ElseIf/Else was found on that line.");

        var and = (BinaryExpressionSyntax)block.IfStatement.Condition;
        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(block));
        var bodyIndent = indentation + "    ";
        var innerIndent = indentation + "        ";
        var body = TrimRegion(original.Substring(block.IfStatement.Span.End,
            block.EndIfStatement.SpanStart - block.IfStatement.Span.End));
        var reindented = body.Length == 0 ? string.Empty : Reindent(body, bodyIndent, innerIndent, newLine);

        var replacement = "If " + and.Left + " Then" + newLine +
                          bodyIndent + "If " + and.Right + " Then" + newLine +
                          (reindented.Length == 0 ? string.Empty : reindented + newLine) +
                          bodyIndent + "End If" + newLine +
                          indentation + "End If";

        return new Outcome(ReplaceNode(target, block, replacement),
            [$"split '{and.Left}' and '{and.Right}' into nested Ifs"]);
    }

    /// <summary><c>If x IsNot Nothing Then x.M()</c> → <c>x?.M()</c>. The body must be the use of x.</summary>
    private Outcome JoinNullCheckWithUsage(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<MultiLineIfBlockSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderByDescending(LineOf)
            .FirstOrDefault(candidate => candidate.ElseIfBlocks.Count == 0 && candidate.ElseBlock is null &&
                                         candidate.Statements.Count == 1 &&
                                         candidate.Statements[0] is ExpressionStatementSyntax &&
                                         candidate.IfStatement.Condition is BinaryExpressionSyntax check &&
                                         check.IsKind(SyntaxKind.IsNotExpression) &&
                                         check.Right is LiteralExpressionSyntax literal &&
                                         literal.IsKind(SyntaxKind.NothingLiteralExpression));

        if (block is null)
            return Outcome.Fail(
                "No 'If x IsNot Nothing Then <one statement>' was found on that line (the body must be a single statement and there is no Else).");

        var condition = (BinaryExpressionSyntax)block.IfStatement.Condition;
        var receiver = condition.Left.ToString().Trim();
        var statement = (ExpressionStatementSyntax)block.Statements[0];
        var body = statement.Expression.ToString().Trim();

        if (!body.StartsWith(receiver + ".", StringComparison.Ordinal))
            return Outcome.Fail(
                $"The body does not start a member access on '{receiver}', so a conditional access would not replace the check.");

        var rewritten = receiver + "?" + body[receiver.Length..];
        var indentation = IndentationAt(target.Text, LineOf(block));
        var newLine = NewLineOf(target.Text.ToString());
        var replacement = rewritten.Replace("\r\n", "\n").Replace("\n", newLine);
        replacement = indentation + replacement;

        return new Outcome(ReplaceNode(target, block, replacement),
            [$"replaced the Nothing check with '{receiver}?.'"]);
    }

    /// <summary>An If/ElseIf chain that compares one selector becomes a <c>Select Case</c>.</summary>
    private Outcome IfToSwitch(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<MultiLineIfBlockSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderBy(candidate => candidate.SpanStart)
            .FirstOrDefault(candidate => candidate.ElseIfBlocks.Count > 0);

        if (block is null)
            return Outcome.Fail("No If/ElseIf chain was found on that line: Select Case needs at least one ElseIf.");

        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(block));

        string? selector = null;
        var cases = new List<(string Value, string Body)>();

        if (!TryComparison(block.IfStatement.Condition, ref selector, out var firstValue))
            return Outcome.Fail("The If condition is not an '=' comparison over a single selector.");

        var boundaries = new List<(int Start, int End)>();
        boundaries.Add((block.IfStatement.Span.End, FirstBoundaryStart(block, 0)));

        for (var index = 0; index < block.ElseIfBlocks.Count; index++)
        {
            var elseIf = block.ElseIfBlocks[index];
            if (!TryComparison(elseIf.ElseIfStatement.Condition, ref selector, out _))
                return Outcome.Fail("An ElseIf condition is not an '=' comparison over the same selector.");
            boundaries.Add((elseIf.ElseIfStatement.Span.End, FirstBoundaryStart(block, index + 1)));
        }

        if (!TryComparison(block.IfStatement.Condition, ref selector, out firstValue))
            return Outcome.Fail("The If condition is not an '=' comparison over a single selector.");
        cases.Add((firstValue!, BodyOf(original, boundaries[0])));

        for (var index = 0; index < block.ElseIfBlocks.Count; index++)
        {
            TryComparison(block.ElseIfBlocks[index].ElseIfStatement.Condition, ref selector, out var value);
            cases.Add((value!, BodyOf(original, boundaries[index + 1])));
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("Select Case ").Append(selector!.Trim()).Append(newLine);
        foreach (var (value, body) in cases)
        {
            builder.Append(indentation).Append("Case ").Append(value.Trim()).Append(newLine);
            if (body.Length > 0) builder.Append(body).Append(newLine);
        }

        if (block.ElseBlock is { } elseBlock)
        {
            var elseBody = TrimRegion(original.Substring(elseBlock.ElseStatement.Span.End,
                block.EndIfStatement.SpanStart - elseBlock.ElseStatement.Span.End));
            builder.Append(indentation).Append("Case Else").Append(newLine);
            if (elseBody.Length > 0) builder.Append(elseBody).Append(newLine);
        }

        builder.Append(indentation).Append("End Select");

        return new Outcome(ReplaceNode(target, block, builder.ToString()),
            [$"If/ElseIf chain over '{selector!.Trim()}' → Select Case with {cases.Count} case(s)"]);

        static bool TryComparison(ExpressionSyntax condition, ref string? selector, out string? value)
        {
            value = null;
            if (condition is not BinaryExpressionSyntax comparison || !comparison.IsKind(SyntaxKind.EqualsExpression))
                return false;

            var left = comparison.Left.ToString().Trim();
            if (selector is null) selector = left;
            else if (!string.Equals(selector, left, StringComparison.Ordinal)) return false;

            value = comparison.Right.ToString();
            return true;
        }

        static int FirstBoundaryStart(MultiLineIfBlockSyntax block, int elseIfIndex) =>
            elseIfIndex < block.ElseIfBlocks.Count
                ? block.ElseIfBlocks[elseIfIndex].SpanStart
                : block.ElseBlock?.SpanStart ?? block.EndIfStatement.SpanStart;

        static string BodyOf(string text, (int Start, int End) boundary) =>
            TrimRegion(text.Substring(boundary.Start, boundary.End - boundary.Start));
    }

    /// <summary>The region without the newlines around it, keeping the indentation of its first line.</summary>
    private static string TrimRegion(string region)
    {
        var start = 0;
        while (start < region.Length && region[start] is '\r' or '\n') start++;

        var end = region.Length;
        while (end > start && char.IsWhiteSpace(region[end - 1])) end--;

        return region[start..end];
    }
}
