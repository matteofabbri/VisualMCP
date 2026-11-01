using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.ContextActions;

public sealed partial class ContextActionService
{
    /// <summary>
    /// ReSharper's "Convert to string concatenation": an interpolated string with at least one hole becomes
    /// <c>"a" + x + "b"</c>. A hole with an alignment or a format specifier has no concatenation form, so the
    /// action is refused instead of dropping it.
    /// </summary>
    private Outcome InterpolatedToConcat(Target target)
    {
        var interpolated = target.Root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (interpolated is null) return Outcome.Fail($"No interpolated string on line {target.Line}.");

        var pieces = new List<string>();
        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    if (text.TextToken.ValueText.Length > 0)
                        pieces.Add(SymbolDisplay.FormatLiteral(text.TextToken.ValueText, quote: true));
                    break;

                case InterpolationSyntax hole:
                    if (hole.AlignmentClause is not null || hole.FormatClause is not null)
                        return Outcome.Fail($"The hole {{{hole}}}}} has an alignment or a format specifier: it has no concatenation form.");
                    pieces.Add(hole.Expression.ToString());
                    break;
            }
        }

        if (pieces.Count < 2 || !interpolated.Contents.OfType<InterpolationSyntax>().Any())
            return Outcome.Fail("The interpolated string has no hole: there is nothing to concatenate.");

        var original = target.Text.ToString();
        var edited = original.Remove(interpolated.SpanStart, interpolated.Span.Length)
            .Insert(interpolated.SpanStart, string.Join(" + ", pieces));

        return new Outcome(edited, [$"interpolated string of {pieces.Count} piece(s) → concatenation"]);
    }

    /// <summary>
    /// ReSharper's "Add digit separators": the integer literal on the line gets a <c>_</c> every three digits
    /// from the right. Hexadecimal, binary, decimal and already-separated literals are left alone.
    /// </summary>
    private Outcome DigitSeparators(Target target)
    {
        var literal = target.Root.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.NumericLiteralToken) && LineOf(token) == target.Line)
            .FirstOrDefault(token => token.Text.All(c => char.IsDigit(c) || char.IsLetter(c)));

        if (string.IsNullOrEmpty(literal.Text))
            return Outcome.Fail($"No plain integer literal on line {target.Line}.");

        var raw = literal.Text;
        if (raw.Contains('_', StringComparison.Ordinal))
            return Outcome.Fail($"'{raw}' already carries digit separators.");
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return Outcome.Fail($"'{raw}' is hexadecimal or binary: grouping it needs a different convention.");

        var suffixStart = raw.Length;
        while (suffixStart > 0 && char.IsLetter(raw[suffixStart - 1]))
            suffixStart--;

        var digits = raw[..suffixStart];
        var suffix = raw[suffixStart..];
        if (digits.Length < 5 || !digits.All(char.IsDigit))
            return Outcome.Fail($"'{raw}' is not an integer with at least five digits: there is nothing to group.");

        var grouped = new System.Text.StringBuilder(digits.Length + digits.Length / 3);
        for (var index = 0; index < digits.Length; index++)
        {
            if (index > 0 && (digits.Length - index) % 3 == 0)
                grouped.Append('_');
            grouped.Append(digits[index]);
        }

        var original = target.Text.ToString();
        var edited = original.Remove(literal.SpanStart, literal.Span.Length)
            .Insert(literal.SpanStart, grouped.ToString() + suffix);

        return new Outcome(edited, [$"'{raw}' → '{grouped}{suffix}'"]);
    }

    /// <summary>
    /// ReSharper's "Remove argument name": a named argument on the line becomes positional. The compiler gate
    /// refuses it when a name that comes after another named argument cannot become positional.
    /// </summary>
    private Outcome RemoveArgumentName(Target target, string? text)
    {
        var requested = text?.Trim();
        var named = target.Root.DescendantNodes().OfType<ArgumentSyntax>()
            .Where(candidate => candidate.NameColon is not null && LineOf(candidate) == target.Line)
            .Where(candidate => requested is null ||
                                string.Equals(candidate.NameColon!.Name.Identifier.ValueText, requested, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (named is null)
            return Outcome.Fail(requested is null
                ? $"No named argument on line {target.Line}."
                : $"No argument named '{requested}' on line {target.Line}.");

        var original = target.Text.ToString();
        var edited = original.Remove(named.SpanStart, named.Span.Length)
            .Insert(named.SpanStart, named.Expression.ToString());

        return new Outcome(edited, [$"argument '{named.NameColon!.Name}' → positional"]);
    }

    /// <summary>
    /// ReSharper's "Check dictionary key": a <c>ContainsKey</c> guard before the statement that indexes a
    /// dictionary with the key named in <c>text</c> (or with the only key on the line).
    /// </summary>
    private Outcome CheckDictionaryKey(Target target, string? text)
    {
        var key = text?.Trim();

        var access = target.Root.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Where(candidate => candidate.ArgumentList.Arguments.Count == 1 && LineOf(candidate) == target.Line)
            .Where(candidate => key is null ||
                                candidate.ArgumentList.Arguments[0].Expression.ToString() == key)
            .Where(candidate => HasContainsKey(target.Model.GetTypeInfo(candidate.Expression).Type))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (access is null)
            return Outcome.Fail($"No dictionary indexer on line {target.Line} (the action needs a type with ContainsKey): pass the key expression in text.");

        var keyExpression = access.ArgumentList.Arguments[0].Expression.ToString();
        var receiver = access.Expression.ToString();

        var statement = target.Root.DescendantNodes().OfType<StatementSyntax>()
            .Where(candidate => candidate.Span.Contains(access.SpanStart))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();
        if (statement is null) return Outcome.Fail("The element access is not inside a statement.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(statement));
        var guard = $"if (!{receiver}.ContainsKey({keyExpression})) " +
                    "throw new KeyNotFoundException(\"The dictionary does not contain the requested key.\");";

        var edited = target.Text.ToString().Insert(statement.SpanStart, guard + newLine + indentation);

        var needed = MissingNamespaces(target, statement.SpanStart, "System.Collections.Generic");
        if (needed.Count > 0 && target.Root is CompilationUnitSyntax unit)
            edited = AddUsings(edited, unit, needed);

        return new Outcome(edited, [$"guarded {receiver}[{keyExpression}] against a missing key"]);
    }

    /// <summary>
    /// ReSharper's "Check enum value is defined": an <c>Enum.IsDefined</c> guard at the top of the method for
    /// the enum parameter named in <c>text</c> (or the only enum parameter).
    /// </summary>
    private Outcome CheckEnumDefined(Target target, string? text)
    {
        var lineStart = target.Text.Lines[target.Line - 1].Start;
        var method = target.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
            .Where(candidate => candidate.FullSpan.Contains(lineStart))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (method is null) return Outcome.Fail($"Line {target.Line} is not inside a method or constructor.");
        if (method.Body is null) return Outcome.Fail("The method is expression-bodied: give it a block body first, then retry.");
        if (target.Model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
            return Outcome.Fail($"Could not resolve the declaration that contains line {target.Line}.");

        var candidates = symbol.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Enum).ToList();
        var requested = text?.Trim();
        if (requested is not null)
            candidates = candidates.Where(parameter => string.Equals(parameter.Name, requested, StringComparison.Ordinal)).ToList();

        if (candidates.Count == 0)
            return Outcome.Fail(requested is null
                ? $"'{symbol.Name}' has no enum parameter to check."
                : $"'{symbol.Name}' has no enum parameter named '{requested}'.");
        if (candidates.Count > 1)
            return Outcome.Fail($"'{symbol.Name}' has {candidates.Count} enum parameters: name one in text.");

        var parameter = candidates[0];
        var typeName = parameter.Type.ToMinimalDisplayString(target.Model, method.SpanStart);
        var name = parameter.Name;

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(method));
        var guard = $"if (!Enum.IsDefined(typeof({typeName}), {name})) " +
                    $"throw new ArgumentOutOfRangeException(nameof({name}), {name}, \"Unknown {typeName} value.\");";

        var open = method.Body.OpenBraceToken.Span.End;
        var edited = method.Body.Statements.Count == 0
            ? target.Text.ToString().Insert(open, newLine + indentation + "    " + guard + newLine + indentation)
            : target.Text.ToString().Insert(open, newLine + indentation + "    " + guard);

        var needed = MissingNamespaces(target, method.SpanStart, "System");
        if (needed.Count > 0 && target.Root is CompilationUnitSyntax unit)
            edited = AddUsings(edited, unit, needed);

        return new Outcome(edited, [$"guarded {name} against an undefined {typeName}"]);
    }

    /// <summary>
    /// ReSharper's "Invert loop": <c>for (var i = 0; i &lt; bound; i++)</c> becomes a descending loop from
    /// <c>bound - 1</c> to zero. Only the canonical ascending shape is accepted; anything else is refused.
    /// </summary>
    private Outcome InvertLoop(Target target)
    {
        var loop = target.Root.DescendantNodes().OfType<ForStatementSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (loop is null) return Outcome.Fail($"No for statement on line {target.Line}.");

        string? name = null;
        string? type = null;
        switch (loop.Declaration?.Variables)
        {
            case { Count: 1 } variables when IsZero(variables[0].Initializer?.Value):
                name = variables[0].Identifier.ValueText;
                type = loop.Declaration!.Type.ToString();
                break;
        }

        if (name is null && loop.Initializers.Count == 1 &&
            loop.Initializers[0] is AssignmentExpressionSyntax assignment &&
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            IsZero(assignment.Right))
        {
            name = assignment.Left.ToString();
        }

        if (name is null)
            return Outcome.Fail($"The for on line {target.Line} does not start at zero (the shape this action inverts).");

        if (loop.Condition is not BinaryExpressionSyntax condition ||
            !condition.IsKind(SyntaxKind.LessThanExpression) ||
            condition.Left.ToString() != name)
            return Outcome.Fail($"The for on line {target.Line} does not count up with '< {name}': nothing to invert.");

        var increments = loop.Incrementors.Count == 1 &&
                         (loop.Incrementors[0].IsKind(SyntaxKind.PostIncrementExpression) ||
                          loop.Incrementors[0].IsKind(SyntaxKind.PreIncrementExpression) ||
                          (loop.Incrementors[0] is AssignmentExpressionSyntax add &&
                           add.IsKind(SyntaxKind.AddAssignmentExpression) && IsOne(add.Right)));

        if (!increments)
            return Outcome.Fail($"The for on line {target.Line} does not step by one: nothing to invert.");

        var bound = condition.Right.ToString();
        var boundText = NeedsParentheses(condition.Right) ? $"({bound})" : bound;
        var initializer = type is null ? $"{name} = {boundText} - 1" : $"{type} {name} = {boundText} - 1";
        var header = $"for ({initializer}; {name} >= 0; {name}--)";

        var original = target.Text.ToString();
        var edited = original.Remove(loop.SpanStart, loop.CloseParenToken.Span.End - loop.SpanStart)
            .Insert(loop.SpanStart, header);

        return new Outcome(edited, [$"ascending loop → descending from {bound} - 1"]);
    }

    private static bool IsZero(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
        literal.Token.ValueText == "0";

    private static bool IsOne(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
        literal.Token.ValueText == "1";

    /// <summary>Whether the bound has to be parenthesised before <c>- 1</c> is appended to it.</summary>
    private static bool NeedsParentheses(ExpressionSyntax bound) =>
        bound is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax
            or LiteralExpressionSyntax or ElementAccessExpressionSyntax or InvocationExpressionSyntax);

    private static bool HasContainsKey(ITypeSymbol? type) =>
        type is not null &&
        type.GetMembers("ContainsKey").OfType<IMethodSymbol>().Any(method => method.Parameters.Length == 1);

    private static int LineOf(SyntaxToken token) =>
        token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
