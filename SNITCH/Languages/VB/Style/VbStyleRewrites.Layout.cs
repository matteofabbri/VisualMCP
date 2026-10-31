using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;
using static Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Snitch.Languages.VB.Style;

// The layout rules. VB writes its blocks with End X instead of braces, so "a blank line after a block"
// and "one blank line at most" are the same two rules with a different separator — and both work on the
// leading trivia of the statement that follows, which is where VB keeps the blank lines.

/// <summary>The whitespace questions the layout rules ask; a comment always stops a collapse.</summary>
internal static class VbLayout
{
    public static bool HasComment(SyntaxTriviaList trivia) => trivia.Any(item => item.Kind() is
        SyntaxKind.CommentTrivia or SyntaxKind.DocumentationCommentTrivia or SyntaxKind.DisabledTextTrivia);

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

        return SyntaxFactory.TriviaList(kept);
    }

    /// <summary>
    /// One line break in front of the indentation. In VB the terminator that ends a line is the trailing
    /// trivia of the line's last token, so a statement that follows a block has no line break of its own:
    /// the text to write is the one the previous line used.
    /// </summary>
    public static SyntaxTriviaList InsertLineBreak(SyntaxTriviaList leading, string newLine) =>
        leading.Insert(0, SyntaxFactory.EndOfLine(newLine));

    /// <summary>
    /// The node to replace so a trivia edit inside <paramref name="node"/> survives: the engine rebuilds
    /// the surrounding statement list from the original trivia, so the anchor is the closest ancestor
    /// that is not a statement — in VB that is usually the compilation unit.
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

    /// <summary>A match anchored where the engine can keep the trivia: the anchor with the node replaced.</summary>
    public static StyleMatch Anchored(StyleRule rule, SyntaxNode anchor, SyntaxNode node, SyntaxNode replacement)
    {
        var rebuilt = anchor.ReplaceNode(node, replacement);
        var line = anchor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new StyleMatch(rule, anchor, rebuilt, line, anchor.ToString(), rebuilt.ToString());
    }
}

/// <summary>IDE2000: more than one blank line becomes one, when the repository forbids the extra lines.</summary>
public sealed class MultipleBlankLinesRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2000", "Remove multiple blank lines", "two blank lines → one");

    public override string? RequiredOption => "dotnet_style_allow_multiple_blank_lines_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowMultipleBlankLines != false) return null;
        if (node is not StatementSyntax statement) return null;

        // A method block and the statement that opens it start at the same token, so both carry the
        // same leading trivia and both would anchor the same whole-file match. Only the outer one.
        if (statement.Parent is not null && statement.Parent.SpanStart == statement.SpanStart) return null;

        var leading = statement.GetLeadingTrivia();
        if (VbLayout.HasComment(leading) || VbLayout.EndOfLineCount(leading) < 2) return null;

        var anchor = VbLayout.AnchorFor(statement);
        if (anchor is null) return null;

        return VbLayout.Anchored(Rule, anchor, statement,
            statement.WithLeadingTrivia(VbLayout.Collapse(leading, 1)));
    }
}

/// <summary>IDE2003: a blank line between a block and the statement that follows it, when the repository asks for one.</summary>
public sealed class BlankLineAfterBlockRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE2003", "Add a blank line after a block", "End If next → End If blank line next");

    public override string? RequiredOption => "dotnet_style_allow_statement_immediately_after_block_experimental";

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Options.AllowStatementImmediatelyAfterBlock != false) return null;
        if (node is not StatementSyntax statement || statement.Parent is null) return null;
        if (statement.Parent.SpanStart == statement.SpanStart) return null;

        var siblings = VbBlocks.StatementsOf(statement.Parent);
        var index = VbStyleFacts.IndexOf(siblings, statement);
        if (index <= 0) return null;
        if (siblings[index - 1] is not StatementSyntax previous || !EndsABlock(previous)) return null;

        // The blank line this rule adds is one the statement does not already have: VB puts the
        // terminator of the previous line in that line's trailing trivia, so "no blank line" is zero
        // end-of-line trivia here.
        var leading = statement.GetLeadingTrivia();
        if (VbLayout.HasComment(leading) || VbLayout.EndOfLineCount(leading) != 0) return null;

        var newLine = previous.GetLastToken().TrailingTrivia
            .FirstOrDefault(item => item.IsKind(SyntaxKind.EndOfLineTrivia)).ToString();
        if (string.IsNullOrEmpty(newLine)) newLine = Environment.NewLine;

        var anchor = VbLayout.AnchorFor(statement);
        if (anchor is null) return null;

        return VbLayout.Anchored(Rule, anchor, statement,
            statement.WithLeadingTrivia(VbLayout.InsertLineBreak(leading, newLine)));
    }

    private static bool EndsABlock(StatementSyntax statement) => statement is
        MethodBlockBaseSyntax or MultiLineIfBlockSyntax or ForOrForEachBlockSyntax or WhileBlockSyntax
        or DoLoopBlockSyntax or UsingBlockSyntax or SyncLockBlockSyntax or WithBlockSyntax or TryBlockSyntax
        or SelectBlockSyntax or TypeBlockSyntax;
}

/// <summary>
/// IDE0073: the repository's <c>file_header_template</c> is written at the top of a file that does not
/// start with it. The rule needs the option, so a repository that did not write one is left alone.
/// </summary>
public sealed class FileHeaderRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0073", "Add a file header", "the file_header_template is written above the first line");

    public override string? RequiredOption => "file_header_template";

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var header = context.Options.FileHeader;
        if (string.IsNullOrWhiteSpace(header)) return null;
        if (node is not CompilationUnitSyntax unit) return null;

        var text = unit.ToString();
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var expected = string.Join(newLine, header.Replace("\r\n", "\n").Split('\n'));

        if (text.StartsWith(expected, StringComparison.Ordinal)) return null;

        var replacement = ParseCompilationUnit(expected + newLine + newLine + text);
        return new StyleMatch(Rule, unit, replacement, 1, "(no header)", expected);
    }
}
