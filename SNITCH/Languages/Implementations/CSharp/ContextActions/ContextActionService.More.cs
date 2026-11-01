using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.ContextActions;

public sealed partial class ContextActionService
{
    /// <summary>
    /// ReSharper's "Add braces": the embedded statement of an if/else/for/foreach/while/using/lock gets a
    /// block. The body keeps its formatting and is re-indented one level.
    /// </summary>
    private Outcome AddBraces(Target target)
    {
        StatementSyntax? embedded = null;

        foreach (var statement in target.Root.DescendantNodes().OfType<StatementSyntax>())
        {
            if (LineOf(statement) != target.Line) continue;

            var candidate = statement switch
            {
                IfStatementSyntax branch => branch.Statement,
                ForStatementSyntax loop => loop.Statement,
                ForEachStatementSyntax loop => loop.Statement,
                WhileStatementSyntax loop => loop.Statement,
                DoStatementSyntax loop => loop.Statement,
                UsingStatementSyntax use => use.Statement,
                LockStatementSyntax guard => guard.Statement,
                _ => null,
            };

            if (candidate is null || candidate is BlockSyntax) continue;
            if (LineOf(candidate) != target.Line) continue;

            embedded = candidate;
        }

        // The else branch is an ElseClauseSyntax, not a statement of its own.
        if (embedded is null)
        {
            foreach (var clause in target.Root.DescendantNodes().OfType<ElseClauseSyntax>())
            {
                if (clause.Statement is BlockSyntax) continue;
                if (LineOf(clause) != target.Line && LineOf(clause.Statement) != target.Line) continue;
                embedded = clause.Statement;
            }
        }

        if (embedded is null)
            return Outcome.Fail($"No brace-less embedded statement on line {target.Line} " +
                                "(it is already a block, or the line is not under an if/else/for/foreach/while/using/lock).");

        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(embedded));
        var body = IndentBlock(original.Substring(embedded.SpanStart, embedded.Span.Length), indentation, newLine);

        var replacement = "{" + newLine + body + newLine + indentation + "}";
        var text = original.Remove(embedded.SpanStart, embedded.Span.Length).Insert(embedded.SpanStart, replacement);

        return new Outcome(text, ["wrapped the embedded statement in braces"]);
    }

    /// <summary>ReSharper's "Surround with #region": a region around the selected whole lines.</summary>
    private Outcome WrapInRegion(Target target, string? text)
    {
        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var lines = original.Split(["\r\n", "\n"], StringSplitOptions.None);

        var from = Math.Min(target.Line, lines.Length);
        var to = Math.Min(Math.Max(target.ToLine, from), lines.Length);
        if (from < 1 || to < 1) return Outcome.Fail("The line range is outside the file.");

        var name = string.IsNullOrWhiteSpace(text) ? "region" : text.Trim();
        var indentation = new string(lines[from - 1].TakeWhile(char.IsWhiteSpace).ToArray());

        // Insert the end first: the start insertion shifts every later line.
        lines = [.. lines[..to], indentation + "#endregion", .. lines[to..]];
        lines = [.. lines[..(from - 1)], indentation + "#region " + name, .. lines[(from - 1)..]];

        return new Outcome(string.Join(newLine, lines), [$"wrapped lines {from}-{to} in #region {name}"]);
    }

    /// <summary>
    /// ReSharper's "Convert foreach to for" for an indexable source (an array or an IList): the loop keeps
    /// the element variable and binds it to <c>source[i]</c> at the top of the body.
    /// </summary>
    private Outcome ForeachToFor(Target target)
    {
        var loop = target.Root.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (loop is null) return Outcome.Fail($"No foreach statement on line {target.Line}.");

        var type = target.Model.GetTypeInfo(loop.Expression).Type;
        var countMember = CountMemberOf(type);
        if (countMember is null)
            return Outcome.Fail(
                $"'{(type?.ToDisplayString() ?? loop.Expression.ToString())}' is not an array or an IList: " +
                "a foreach over any other sequence has no index to count with.");

        var index = UniqueIndexName(target, loop.SpanStart);

        var inner = loop.Statement is BlockSyntax block
            ? string.Join(NewLineOf(target.Text.ToString()),
                block.Statements.Select(statement => statement.ToString()))
            : loop.Statement.ToString();

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(loop));
        var innerIndent = indentation + "    ";
        var bodyIndent = innerIndent + "    ";

        var body = string.Join(newLine, inner.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => bodyIndent + line.TrimStart()));

        var replacement =
            $"for (var {index} = 0; {index} < {loop.Expression}.{countMember}; {index}++)" + newLine +
            indentation + "{" + newLine +
            innerIndent + $"{loop.Type} {loop.Identifier} = {loop.Expression}[{index}];" + newLine +
            body + newLine +
            indentation + "}";

        var original = target.Text.ToString();
        var text = original.Remove(loop.SpanStart, loop.Span.Length).Insert(loop.SpanStart, replacement);

        return new Outcome(text, [$"foreach over {loop.Expression} → for with {index} < {loop.Expression}.{countMember}"]);
    }

    /// <summary>
    /// ReSharper's "Convert to interpolated string": a concatenation with at least one string literal
    /// becomes <c>$"…"</c>. Literals that cannot sit inside an interpolated string are refused.
    /// </summary>
    private Outcome ConcatToInterpolated(Target target)
    {
        var addition = target.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(candidate => candidate.IsKind(SyntaxKind.AddExpression) && LineOf(candidate) == target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (addition is null) return Outcome.Fail($"No '+' concatenation on line {target.Line}.");

        var pieces = new List<SyntaxNode>();
        Flatten(addition, pieces);

        if (pieces.Count < 2) return Outcome.Fail("The concatenation has a single piece.");
        if (!pieces.Any(piece => piece is LiteralExpressionSyntax { Token.ValueText: { } } literal &&
                                 literal.IsKind(SyntaxKind.StringLiteralExpression)))
            return Outcome.Fail("The concatenation has no string literal: there is nothing to make interpolated.");

        var builder = new System.Text.StringBuilder("$\"");
        foreach (var piece in pieces)
        {
            if (piece is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var value = literal.Token.ValueText;
                if (value.Contains('{') || value.Contains('}') || value.Contains('"'))
                    return Outcome.Fail($"The literal '{literal.Token.Text}' cannot be embedded verbatim: it holds a quote or a brace.");

                builder.Append(value);
                continue;
            }

            builder.Append('{').Append(piece.ToString()).Append('}');
        }

        builder.Append('"');

        var original = target.Text.ToString();
        var text = original.Remove(addition.SpanStart, addition.Span.Length).Insert(addition.SpanStart, builder.ToString());

        return new Outcome(text, [$"concatenation of {pieces.Count} piece(s) → interpolated string"]);
    }

    /// <summary>
    /// ReSharper's "Check index for out of range": a guard before the statement, for an element access
    /// indexed by the variable named in <c>text</c>.
    /// </summary>
    private Outcome CheckIndexRange(Target target, string? text)
    {
        var index = text?.Trim();

        var access = target.Root.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .Where(candidate => index is null ||
                                candidate.ArgumentList.Arguments.Any(argument =>
                                    argument.Expression.ToString() == index))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (access is null)
            return Outcome.Fail($"No element access on line {target.Line}: pass the index variable name in text.");

        var indexExpression = index ?? access.ArgumentList.Arguments[0].Expression.ToString();
        var collection = access.Expression.ToString();
        var countMember = CountMemberOf(target.Model.GetTypeInfo(access.Expression).Type) ?? "Length";

        var statement = target.Root.DescendantNodes().OfType<StatementSyntax>()
            .Where(candidate => candidate.Span.Contains(access.SpanStart))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (statement is null) return Outcome.Fail("The element access is not inside a statement.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(statement));
        var guard = $"if ({indexExpression} < 0 || {indexExpression} >= {collection}.{countMember}) " +
                    $"throw new ArgumentOutOfRangeException(nameof({indexExpression}));";

        var original = target.Text.ToString();
        var edited = original.Insert(statement.SpanStart, guard + newLine + indentation);

        var needed = MissingNamespaces(target, statement.SpanStart, "System");
        if (needed.Count > 0 && target.Root is CompilationUnitSyntax unit)
            edited = AddUsings(edited, unit, needed);

        return new Outcome(edited, [$"guarded {indexExpression} against {collection}.{countMember}"]);
    }

    /// <summary>
    /// ReSharper's "Convert for to foreach": <c>for (var i = 0; i &lt; xs.Length; i++)</c> over an
    /// indexable source becomes <c>foreach (var item in xs)</c>, and every <c>xs[i]</c> in the body
    /// becomes <c>item</c>. Refused when the index is used for anything else.
    /// </summary>
    private Outcome ForToForeach(Target target)
    {
        var loop = target.Root.DescendantNodes().OfType<ForStatementSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (loop is null) return Outcome.Fail($"No for statement on line {target.Line}.");

        if (loop.Declaration is not { Variables.Count: 1 } declaration)
            return Outcome.Fail("The for loop must declare its index (for (var i = 0; …)).");

        var variable = declaration.Variables[0];
        var indexName = variable.Identifier.ValueText;

        if (variable.Initializer?.Value.ToString() != "0")
            return Outcome.Fail($"The index must start at 0 (it starts at '{variable.Initializer?.Value}').");

        if (loop.Condition is not BinaryExpressionSyntax condition ||
            !condition.IsKind(SyntaxKind.LessThanExpression) ||
            condition.Left.ToString() != indexName)
            return Outcome.Fail("The condition must be '<index> < <collection>.Length' or '.Count'.");

        var collection = condition.Right switch
        {
            MemberAccessExpressionSyntax access when access.Name.Identifier.ValueText is "Length" or "Count"
                => access.Expression.ToString(),
            _ => null,
        };

        if (collection is null)
            return Outcome.Fail("The condition must compare against <collection>.Length or <collection>.Count.");

        if (loop.Incrementors.Count != 1 ||
            loop.Incrementors[0] is not PostfixUnaryExpressionSyntax increment ||
            !increment.IsKind(SyntaxKind.PostIncrementExpression) ||
            increment.Operand.ToString() != indexName)
            return Outcome.Fail("The incrementor must be '<index>++'.");

        var body = loop.Statement;
        var accesses = body.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Where(access => access.Expression.ToString() == collection &&
                             access.ArgumentList.Arguments.Count == 1 &&
                             access.ArgumentList.Arguments[0].Expression.ToString() == indexName)
            .ToList();

        if (accesses.Count == 0)
            return Outcome.Fail($"The body never reads '{collection}[{indexName}]': there is nothing to translate.");

        var otherUses = body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Identifier.ValueText == indexName &&
                                 !accesses.Any(access => access.ArgumentList.Span.Contains(identifier.Span)))
            .ToList();

        if (otherUses.Count > 0)
            return Outcome.Fail($"The index '{indexName}' is used for something other than indexing: the loop is not a scan.");

        var element = UniqueElementName(body, ["item", "element", "value"]);
        var original = target.Text.ToString();

        // Body changes first (their spans are later in the text), then the header.
        var edited = original;
        foreach (var access in accesses.OrderByDescending(access => access.SpanStart))
            edited = edited.Remove(access.SpanStart, access.Span.Length).Insert(access.SpanStart, element);

        var headerEnd = loop.CloseParenToken.Span.End;
        edited = edited.Remove(loop.SpanStart, headerEnd - loop.SpanStart)
            .Insert(loop.SpanStart, $"foreach (var {element} in {collection})");

        return new Outcome(edited, [$"for over {collection} → foreach with {element}"]);
    }

    /// <summary>
    /// ReSharper's "Convert if to switch" for an equality chain on one subject:
    /// <c>if (x == 1) … else if (x == 2) … else …</c> becomes a switch. Refused when the conditions
    /// are not all <c>subject == constant</c>, because a switch only expresses those.
    /// </summary>
    private Outcome IfChainToSwitch(Target target)
    {
        var first = target.Root.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (first is null) return Outcome.Fail($"No if statement on line {target.Line}.");
        if (first.Else is null) return Outcome.Fail("The if has no else branch: a single branch is not a chain.");

        var clauses = new List<(ExpressionSyntax Condition, StatementSyntax Body)>();
        StatementSyntax? defaultBody = null;
        var current = first;
        string? subject = null;

        while (current is not null)
        {
            if (current.Condition is not BinaryExpressionSyntax binary ||
                !binary.IsKind(SyntaxKind.EqualsExpression))
                return Outcome.Fail($"'{current.Condition}' is not an equality: a switch only expresses 'x == constant'.");

            var left = binary.Left.ToString();
            subject ??= left;
            if (left != subject)
                return Outcome.Fail("The conditions compare different expressions; a switch needs one subject.");

            var constant = target.Model.GetConstantValue(binary.Right);
            if (!constant.HasValue)
                return Outcome.Fail($"'{binary.Right}' is not a compile-time constant, so it cannot be a case label.");

            clauses.Add((binary.Right, current.Statement));

            if (current.Else?.Statement is IfStatementSyntax next)
            {
                current = next;
                continue;
            }

            defaultBody = current.Else?.Statement;
            current = null;
        }

        if (clauses.Count < 2)
            return Outcome.Fail("The chain has a single case: a switch would not read better. Nothing was changed.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(first));
        var inner = indentation + "    ";
        var bodyIndent = inner + "    ";

        var builder = new System.Text.StringBuilder();
        builder.Append($"switch ({subject})").Append(newLine).Append(indentation).Append('{').Append(newLine);

        foreach (var (condition, body) in clauses)
        {
            builder.Append(inner).Append("case ").Append(condition.ToString()).Append(':').Append(newLine);
            builder.Append(inner).Append('{').Append(newLine);
            builder.Append(Statements(body, bodyIndent, newLine));
            if (!EndsWithJump(body)) builder.Append(bodyIndent).Append("break;").Append(newLine);
            builder.Append(inner).Append('}').Append(newLine);
        }

        if (defaultBody is not null)
        {
            builder.Append(inner).Append("default:").Append(newLine);
            builder.Append(inner).Append('{').Append(newLine);
            builder.Append(Statements(defaultBody, bodyIndent, newLine));
            if (!EndsWithJump(defaultBody)) builder.Append(bodyIndent).Append("break;").Append(newLine);
            builder.Append(inner).Append('}').Append(newLine);
        }

        builder.Append(indentation).Append('}');

        var last = first;
        while (last.Else?.Statement is IfStatementSyntax next) last = next;
        var end = (last.Else?.Span.End) ?? last.Span.End;

        var original = target.Text.ToString();
        var edited = original.Remove(first.SpanStart, end - first.SpanStart)
            .Insert(first.SpanStart, builder.ToString());

        return new Outcome(edited, [$"if/else-if chain on {subject} → switch with {clauses.Count} case(s)"]);
    }

    /// <summary>The statements of a branch, each re-indented to the switch's depth.</summary>
    private static string Statements(StatementSyntax body, string indentation, string newLine)
    {
        var texts = body is BlockSyntax block
            ? block.Statements.Select(statement => statement.ToString())
            : [body.ToString()];

        var builder = new System.Text.StringBuilder();
        foreach (var text in texts)
        {
            foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
                builder.Append(indentation).Append(line.TrimStart()).Append(newLine);
        }

        return builder.ToString();
    }

    private static bool EndsWithJump(StatementSyntax body)
    {
        var last = body is BlockSyntax block ? block.Statements.LastOrDefault() : body;

        return last is ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax
            or ContinueStatementSyntax or YieldStatementSyntax or GotoStatementSyntax;
    }

    private static string UniqueElementName(SyntaxNode body, string[] candidates)
    {
        var used = body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(identifier => identifier.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in candidates)
            if (!used.Contains(candidate)) return candidate;

        return "item";
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static void Flatten(SyntaxNode node, List<SyntaxNode> pieces)
    {
        if (node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
        {
            Flatten(binary.Left, pieces);
            Flatten(binary.Right, pieces);
            return;
        }

        pieces.Add(node);
    }

    private static string? CountMemberOf(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol) return "Length";
        if (type is not INamedTypeSymbol named) return null;

        if (named.SpecialType == SpecialType.System_String) return "Length";

        var isList = named.AllInterfaces.Any(candidate =>
            candidate.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IList<T>");
        return isList || named.Name == "List" ? "Count" : null;
    }

    private static string UniqueIndexName(Target target, int position)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in target.Model.LookupSymbols(position))
        {
            used.Add(symbol.Name);
            if (used.Count > 200) break;
        }

        foreach (var candidate in new[] { "i", "j", "k", "index", "itemIndex" })
            if (!used.Contains(candidate)) return candidate;

        return "index";
    }
}
