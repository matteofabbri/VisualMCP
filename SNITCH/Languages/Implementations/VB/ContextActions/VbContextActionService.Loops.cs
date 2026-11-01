using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.ContextActions;

public sealed partial class VbContextActionService
{
    /// <summary>
    /// "Convert For Each to For": an indexed loop over a collection with Count or Length, with the item
    /// assigned from the index at the top of the body.
    /// </summary>
    private Outcome ForeachToFor(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<ForEachBlockSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderBy(candidate => candidate.SpanStart)
            .FirstOrDefault();

        if (block is null) return Outcome.Fail($"No For Each loop on line {target.Line}.");

        var statement = block.ForEachStatement;
        var declaratorText = statement.ControlVariable.ToString().Trim();
        var itemName = statement.ControlVariable is VariableDeclaratorSyntax declarator
            ? declarator.Names.First().Identifier.ValueText
            : declaratorText;

        if (declaratorText.Length == 0 || itemName.Contains(',', StringComparison.Ordinal))
            return Outcome.Fail("The loop declares several control variables: split it first.");

        var collection = statement.Expression.ToString().Trim();
        var count = CountMember(target.Model.GetTypeInfo(statement.Expression).Type);
        if (count is null)
            return Outcome.Fail($"'{collection}' is not a collection with Count or an array with Length: an indexed For cannot visit it.");

        var index = UniqueIndexName(target, itemName);
        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(block));
        var body = BodyLines(target, block.Statements);

        var replacement = "For " + index + " = 0 To " + collection + "." + count + " - 1" + newLine +
                          indentation + "    Dim " + declaratorText + " = " + collection + "(" + index + ")" + newLine +
                          (body.Length == 0 ? string.Empty : body + newLine) +
                          indentation + "Next";

        return new Outcome(ReplaceNode(target, block, replacement),
            [$"For Each {itemName} In {collection} → indexed For over {count}"]);
    }

    /// <summary>
    /// "Convert For to For Each": the indexed loop this engine produces — <c>For i = 0 To c.Count - 1</c>
    /// whose first body statement is <c>Dim x = c(i)</c> — becomes <c>For Each x In c</c>.
    /// </summary>
    private Outcome ForToForeach(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<ForBlockSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderBy(candidate => candidate.SpanStart)
            .FirstOrDefault();

        if (block is null) return Outcome.Fail($"No For loop on line {target.Line}.");

        var header = block.ForStatement;
        if (!IsZero(header.FromValue))
            return Outcome.Fail("The loop does not start at zero: the shape this action converts does.");

        if (header.StepClause is not null && header.StepClause.StepValue.ToString().Trim() != "1")
            return Outcome.Fail("The loop does not step by one: the shape this action converts does.");

        if (Unwrap(header.ToValue) is not BinaryExpressionSyntax bound ||
            !bound.IsKind(SyntaxKind.SubtractExpression) ||
            bound.Right.ToString().Trim() != "1")
            return Outcome.Fail("The loop does not end at a collection's count minus one: nothing to convert.");

        var countAccess = bound.Left.ToString().Trim();
        var dot = countAccess.LastIndexOf('.');
        if (dot <= 0) return Outcome.Fail("The loop bound is not a property of a collection.");

        var collection = countAccess[..dot].Trim();
        var control = header.ControlVariable.ToString().Trim();
        var index = header.ControlVariable is VariableDeclaratorSyntax controlDeclarator
            ? controlDeclarator.Names.First().Identifier.ValueText
            : control;

        if (block.Statements.FirstOrDefault() is not LocalDeclarationStatementSyntax itemDeclaration ||
            itemDeclaration.Declarators.Count != 1 || itemDeclaration.Declarators[0].Names.Count != 1)
            return Outcome.Fail("The body does not start with 'Dim item = collection(index)': nothing to lift into For Each.");

        var item = itemDeclaration.Declarators[0];
        if (item.Initializer?.Value is not InvocationExpressionSyntax access ||
            access.Expression.ToString().Trim() != collection ||
            access.ArgumentList is not { Arguments.Count: 1 } ||
            access.ArgumentList.Arguments[0].GetExpression()?.ToString().Trim() != index)
            return Outcome.Fail($"The body's first declaration does not read {collection}({index}).");

        // The declarator carries the initializer, which is the call being replaced: the For Each control
        // variable is only the name and its As clause.
        var itemText = item.Names[0] + (item.AsClause is null ? string.Empty : " " + item.AsClause);

        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(block));
        var rest = BodyLines(target, block.Statements.Skip(1).ToList());

        var replacement = "For Each " + itemText + " In " + collection + newLine +
                          (rest.Length == 0 ? string.Empty : rest + newLine) +
                          indentation + "Next";

        return new Outcome(ReplaceNode(target, block, replacement),
            [$"indexed For over {collection} → For Each {item.Names[0].Identifier.ValueText}"]);
    }

    /// <summary>"Invert loop": <c>For i = 0 To bound</c> becomes a descending loop from <c>bound - 1</c>.</summary>
    private Outcome InvertLoop(Target target)
    {
        var block = target.Root.DescendantNodes().OfType<ForBlockSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderBy(candidate => candidate.SpanStart)
            .FirstOrDefault();

        if (block is null) return Outcome.Fail($"No For loop on line {target.Line}.");

        var header = block.ForStatement;
        if (!IsZero(header.FromValue))
            return Outcome.Fail($"The For on line {target.Line} does not start at zero (the shape this action inverts).");

        if (header.StepClause is not null && header.StepClause.StepValue.ToString().Trim() != "1")
            return Outcome.Fail($"The For on line {target.Line} does not step by one: nothing to invert.");

        var bound = header.ToValue.ToString().Trim();
        var boundText = NeedsParentheses(header.ToValue) ? "(" + bound + ")" : bound;
        var replacement = "For " + header.ControlVariable + " = " + boundText + " - 1 To 0 Step -1";

        return new Outcome(ReplaceNode(target, header, replacement),
            [$"ascending For → descending from {bound} - 1"]);
    }

    private static string BodyLines(Target target, IReadOnlyList<StatementSyntax> statements)
    {
        if (statements.Count == 0) return string.Empty;

        var text = target.Text.ToString();
        var lines = statements.Select(statement =>
        {
            var start = target.Text.Lines.GetLineFromPosition(statement.SpanStart).Start;
            var end = target.Text.Lines.GetLineFromPosition(statement.Span.End).End;
            return text.Substring(start, end - start);
        });

        return string.Join(NewLineOf(text), lines);
    }

    private static string UniqueIndexName(Target target, string avoid)
    {
        var used = target.Root.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(identifier => identifier.Identifier.ValueText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = "i";
        var suffix = 1;
        while (used.Contains(candidate) || string.Equals(candidate, avoid, StringComparison.OrdinalIgnoreCase))
            candidate = "i" + suffix++;

        return candidate;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) =>
        expression is ParenthesizedExpressionSyntax parenthesized ? Unwrap(parenthesized.Expression) : expression;

    private static bool IsZero(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
        literal.Token.ValueText == "0";
}
