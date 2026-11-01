using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.ContextActions;

public sealed partial class ContextActionService
{
    private enum WrapKind
    {
        TryCatch,
        TryFinally,
        If,
        Using,
    }

    /// <summary>
    /// ReSharper's "Surround with": wraps the statements that sit entirely on the given lines. The range
    /// must select whole, contiguous statements; the body keeps its own formatting and is re-indented one
    /// level, so the diff is the wrapper and not the code inside it.
    /// </summary>
    private Outcome Wrap(Target target, string? argument, WrapKind kind)
    {
        if (kind is WrapKind.If or WrapKind.Using && (argument is null || !Parses(argument)))
            return Outcome.Fail($"'{argument}' is not a valid C# expression for the wrap.");

        var block = target.Root.DescendantNodes().OfType<BlockSyntax>()
            .Where(candidate => candidate.Statements.Count > 0 &&
                                LineOf(candidate.Statements[0]) <= target.Line &&
                                EndLine(candidate.Statements[candidate.Statements.Count - 1]) >= target.ToLine)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (block is null)
            return Outcome.Fail($"No block covers lines {target.Line}-{target.ToLine}.");

        var selected = block.Statements
            .Where(statement => LineOf(statement) >= target.Line && EndLine(statement) <= target.ToLine)
            .ToList();

        if (selected.Count == 0)
            return Outcome.Fail($"No statement sits entirely on lines {target.Line}-{target.ToLine}: the range cuts one in half.");

        var firstIndex = block.Statements.IndexOf(selected[0]);
        for (var index = 1; index < selected.Count; index++)
        {
            if (block.Statements.IndexOf(selected[index]) != firstIndex + index)
                return Outcome.Fail("The range selects a non-contiguous set of statements: a nested block sits between them.");
        }

        var first = selected[0];
        var last = selected[selected.Count - 1];
        var start = first.SpanStart;
        var end = last.Span.End;
        var original = target.Text.ToString();
        var newLine = NewLineOf(original);
        var indentation = IndentationAt(target.Text, LineOf(first));
        var body = IndentBlock(original.Substring(start, end - start), indentation, newLine);

        var opening = kind switch
        {
            WrapKind.If => $"if ({argument})",
            WrapKind.Using => $"using ({argument})",
            _ => "try",
        };

        var closing = kind switch
        {
            WrapKind.TryCatch =>
                $"}}{newLine}{indentation}catch (Exception){newLine}{indentation}{{{newLine}{indentation}    throw;{newLine}{indentation}}}",
            WrapKind.TryFinally =>
                $"}}{newLine}{indentation}finally{newLine}{indentation}{{{newLine}{indentation}}}",
            _ => "}",
        };

        var replacement = opening + newLine + indentation + "{" + newLine + body + newLine + indentation + closing;
        var text = original.Remove(start, end - start).Insert(start, replacement);

        if (kind == WrapKind.TryCatch)
        {
            var needed = MissingNamespaces(target, first.SpanStart, "System");
            if (needed.Count > 0 && target.Root is CompilationUnitSyntax unit)
                text = AddUsings(text, unit, needed);
        }

        return new Outcome(text, [$"wrapped {selected.Count} statement(s) in {opening.Split(' ')[0]}"]);
    }
}
