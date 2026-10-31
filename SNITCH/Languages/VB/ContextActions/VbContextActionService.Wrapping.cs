using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.ContextActions;

public sealed partial class VbContextActionService
{
    private enum WrapKind
    {
        TryCatch,
        TryFinally,
        If,
        Using,
    }

    /// <summary>
    /// "Surround with": wraps the statements that sit entirely on the given lines in a Try/Catch,
    /// Try/Finally, If or Using block. The range must select whole, contiguous statements, and the body
    /// is re-indented one level so the diff is the wrapper rather than the code inside it.
    /// </summary>
    private Outcome Wrap(Target target, string? argument, WrapKind kind)
    {
        if (kind is WrapKind.If or WrapKind.Using && (argument is null || !Parses(argument)))
            return Outcome.Fail($"'{argument}' is not a valid Visual Basic expression for the wrap.");

        var selection = SelectStatements(target);
        if (selection is null)
            return Outcome.Fail($"No statement block covers lines {target.Line}-{target.ToLine}.");
        if (selection.Value.Selected.Count == 0)
            return Outcome.Fail($"No statement sits entirely on lines {target.Line}-{target.ToLine}: the range cuts one in half.");

        var selected = selection.Value.Selected;
        var first = selected[0];
        var last = selected[^1];

        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(first));
        var body = original.Substring(first.SpanStart, last.Span.End - first.SpanStart);
        var indented = IndentSelected(body, indentation, indentation, newLine);

        var replacement = kind switch
        {
            WrapKind.TryCatch =>
                "Try" + newLine + indented + newLine +
                indentation + "Catch ex As Exception" + newLine +
                indentation + "    Throw" + newLine +
                indentation + "End Try",
            WrapKind.TryFinally =>
                "Try" + newLine + indented + newLine +
                indentation + "Finally" + newLine +
                indentation + "End Try",
            WrapKind.If =>
                "If " + argument + " Then" + newLine + indented + newLine +
                indentation + "End If",
            _ =>
                "Using " + argument + newLine + indented + newLine +
                indentation + "End Using",
        };

        var text = original.Remove(first.SpanStart, last.Span.End - first.SpanStart)
            .Insert(first.SpanStart, replacement);

        var name = kind switch
        {
            WrapKind.TryCatch => "Try/Catch",
            WrapKind.TryFinally => "Try/Finally",
            WrapKind.If => "If",
            _ => "Using",
        };

        return new Outcome(text, [$"wrapped {selected.Count} statement(s) in {name}"]);
    }

    /// <summary>
    /// "Surround with #Region": the directive pair goes around the selected whole lines. Directives are
    /// trivia, so this is a line splice rather than a node rewrite.
    /// </summary>
    private Outcome WrapInRegion(Target target, string? text)
    {
        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var name = string.IsNullOrWhiteSpace(text) ? "Region" : text.Trim().Replace("\"", "\"\"", StringComparison.Ordinal);
        var indentation = IndentationAt(target.Text, target.Line);

        var startLine = target.Text.Lines[target.Line - 1];
        var endLine = target.Text.Lines[target.ToLine - 1];

        var closing = indentation + "#End Region" + newLine;
        var opening = indentation + "#Region \"" + name + "\"" + newLine;

        var end = endLine.EndIncludingLineBreak;
        var edited = original.Insert(end, closing);
        edited = edited.Insert(startLine.Start, opening);

        return new Outcome(edited,
            [$"wrapped lines {target.Line}-{target.ToLine} in #Region \"{name}\""]);
    }

    /// <summary>
    /// Indents a statement range one level: the first line of the range carries no leading whitespace, the
    /// following ones still carry the source indentation, which is removed before the new prefix is added.
    /// </summary>
    private static string IndentSelected(string body, string baseIndent, string indentation, string newLine)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var rebuilt = new List<string>(lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (index > 0 && line.StartsWith(baseIndent, StringComparison.Ordinal))
                line = line[baseIndent.Length..];

            rebuilt.Add(line.Length == 0 ? line : indentation + "    " + line);
        }

        return string.Join(newLine, rebuilt);
    }
}
