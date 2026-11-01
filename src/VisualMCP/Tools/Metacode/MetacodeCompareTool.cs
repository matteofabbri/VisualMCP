using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VisualMCP.Formatting;
using VisualMCP.Translation;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class MetacodeCompareTool
{
    /// <summary>LCS cells beyond this and the alignment is abandoned: 2000×2000 lines is already 16 MB of ints.</summary>
    private const int MaxAlignCells = 4_000_000;

    [McpServerTool(Name = "metacode_compare"), Description(
        "Translate TWO sources into canonical pseudocode and diff the result line by line — the equivalence check. Each " +
        "side is read independently (path, or text + language: C#, Visual Basic, Java, JavaScript, a Razor page, Python, " +
        "C/C++, a Ghidra snapshot, or canonical pseudocode), then both are lowered into the same model and printed, so the " +
        "same algorithm comes out identical while a real difference shows as -/+ lines. A `.py`/`.cpp` side, or language " +
        "'python'/'cpp' with source text, gets its AST dump produced for you (CPython's ast, `clang -Xclang " +
        "-ast-dump=json`); pass the dump itself to skip the external process. Use it to review a translation someone wrote " +
        "by hand — a C++ algorithm and its C# port included, or a JavaScript function and the TypeScript port of it — to " +
        "check that a refactor kept the shape of a body, or to see what a change did. Comparison is the rendered text, " +
        "which is this library's own definition of equivalence (same body, line for line); diagnostics of either side (RAW " +
        "constructs) are listed after the diff, because two texts can agree only by both being incomplete in the same place. " +
        "Leading indentation is ignored when ALIGNING — it is the depth of a line in its own language, not part of what the " +
        "code does, so a C# method inside a class compares with the C++ function it ports — while the lines are printed as " +
        "they are.")]
    public static async Task<CallToolResult> Compare(
        ISourceDumpTranslationService dumps,
        [Description("Left/baseline file. Omit when passing leftText.")] string? leftPath = null,
        [Description("Left/baseline source text.")] string? leftText = null,
        [Description("For leftText (required), or to override leftPath's extension: 'cs', 'vb', 'java', 'js', 'cpp', 'python', " +
                     "'razor', 'ghidra' or 'pseudo'.")]
        string? leftLanguage = null,
        [Description("Right/current file. Omit when passing rightText.")] string? rightPath = null,
        [Description("Right/current source text.")] string? rightText = null,
        [Description("For rightText (required), or to override rightPath's extension: 'cs', 'vb', 'java', 'js', 'cpp', 'python', " +
                     "'razor', 'ghidra' or 'pseudo'.")]
        string? rightLanguage = null,
        [Description("Prefix every declaration and statement with '// @file:line' on both sides, so a mismatch names the line it came from.")]
        bool showOrigin = false,
        [Description("Keep whatever comments the frontends can see (default true); comments are part of the compared text.")]
        bool includeComments = true,
        [Description("Normalize a C-style for into a WHILE on both sides (default true) — without it a for and a while differ by design.")]
        bool normalizeControlFlow = true,
        [Description("Fold a block whose only statement is a block into its parent, on both sides (default true).")]
        bool flattenNestedBlocks = true,
        [Description("Left side, C++ only: keep just the declarations whose qualified name contains this text " +
                     "(clang -ast-dump-filter), which is how a file that includes the standard library stays readable.")]
        string? leftFilter = null,
        [Description("Right side, C++ only: same as leftFilter.")]
        string? rightFilter = null,
        [Description("C++ only, both sides: extra clang arguments for the project, passed verbatim and resolved " +
                     "from each file's folder — '-std=c++20 -I../include'.")]
        string? clangArgs = null,
        [Description("Timeout in seconds per side for the Python/C++ dumper when that side needs one (default 120, max 900).")]
        int timeoutSeconds = 120)
    {
        var (left, leftLoadError) = await MetacodeToolHelpers.LoadAsync(
            leftPath, leftText, leftLanguage, "left", dumps, timeoutSeconds,
            IsCppSide(leftPath, leftLanguage) ? leftFilter : null,
            IsCppSide(leftPath, leftLanguage) ? clangArgs : null);
        if (left is null) return ToolText.Fail(leftLoadError!);

        var (right, rightLoadError) = await MetacodeToolHelpers.LoadAsync(
            rightPath, rightText, rightLanguage, "right", dumps, timeoutSeconds,
            IsCppSide(rightPath, rightLanguage) ? rightFilter : null,
            IsCppSide(rightPath, rightLanguage) ? clangArgs : null);
        if (right is null) return ToolText.Fail(rightLoadError!);

        var (leftDocument, leftRendered, leftError) = MetacodeToolHelpers.Render(
            left, showOrigin, includeComments, normalizeControlFlow, flattenNestedBlocks);
        if (leftDocument is null || leftRendered is null) return ToolText.Fail($"left: {leftError}");

        var (rightDocument, rightRendered, rightError) = MetacodeToolHelpers.Render(
            right, showOrigin, includeComments, normalizeControlFlow, flattenNestedBlocks);
        if (rightDocument is null || rightRendered is null) return ToolText.Fail($"right: {rightError}");

        var leftLines = SplitLines(leftRendered);
        var rightLines = SplitLines(rightRendered);

        // Leading indentation is the depth of a line in ITS language: a C# method sits inside a class
        // inside a namespace, the C++ function it is compared with does not, and the body is the same
        // either way. The lines are still PRINTED as they are; only the alignment ignores the indent.
        var leftKeys = Keys(leftLines);
        var rightKeys = Keys(rightLines);

        var text = new StringBuilder();
        text.Append("--- ").Append(MetacodeToolHelpers.Header(leftDocument, leftLines.Length)).Append('\n');
        text.Append("+++ ").Append(MetacodeToolHelpers.Header(rightDocument, rightLines.Length)).Append('\n');

        if (leftKeys.SequenceEqual(rightKeys, StringComparer.Ordinal))
        {
            text.Append("\nidentical: the two sources produce the same pseudocode.");
        }
        else
        {
            var ops = Align(leftLines, leftKeys, rightLines, rightKeys, out var tooLarge);
            var removed = ops.Count(op => op.Kind == '-');
            var added = ops.Count(op => op.Kind == '+');

            if (tooLarge)
            {
                text.Append("\nnot aligned: ").Append(leftLines.Length).Append(" vs ").Append(rightLines.Length)
                    .Append(" lines is too large to diff cell by cell. ")
                    .Append("Translate one side and compare the two texts with search_text, or narrow the input to one declaration.");
            }
            else
            {
                text.Append('\n').Append(removed).Append(" line(s) only in left, ")
                    .Append(added).Append(" line(s) only in right — '-' is left, '+' is right.\n\n");
                WriteDiff(text, ops);
            }
        }

        var leftDiagnostics = MetacodeToolHelpers.DiagnosticsBlock(leftDocument);
        var rightDiagnostics = MetacodeToolHelpers.DiagnosticsBlock(rightDocument);
        if (leftDiagnostics.Length > 0) text.Append("\nleft ").Append(leftDiagnostics);
        if (rightDiagnostics.Length > 0) text.Append("\nright ").Append(rightDiagnostics);

        return ToolText.Ok(text.ToString());
    }

    /// <summary>
    /// Whether a side is C++, so the clang-only arguments reach that side and only that side: a comparison
    /// pairs a <c>.cpp</c> with a <c>.cs</c> as a rule, and the C# side has no use for them.
    /// </summary>
    private static bool IsCppSide(string? path, string? language)
    {
        var explicitTag = language?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(explicitTag))
            return explicitTag is "cpp" or "c++" or "cxx" or "c";

        if (string.IsNullOrWhiteSpace(path))
            return false;

        return Path.GetExtension(path).ToLowerInvariant() is
            ".cpp" or ".cc" or ".cxx" or ".c++" or ".c" or ".hpp" or ".hh" or ".hxx" or ".h" or ".ipp" or ".inl";
    }

    private static string[] SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The comparison key of a line: what it says, not how deep it is nested.</summary>
    private static string[] Keys(string[] lines)
    {
        var keys = new string[lines.Length];
        for (var index = 0; index < lines.Length; index++)
            keys[index] = lines[index].Trim();
        return keys;
    }

    /// <summary>
    /// A longest-common-subsequence alignment over the two line lists, after stripping the equal prefix and
    /// suffix (which is what keeps the cost down for two spellings of the same algorithm).
    /// </summary>
    private static List<(char Kind, string Line)> Align(
        IReadOnlyList<string> left, IReadOnlyList<string> leftKeys,
        IReadOnlyList<string> right, IReadOnlyList<string> rightKeys,
        out bool tooLarge)
    {
        tooLarge = false;
        var ops = new List<(char Kind, string Line)>();

        var start = 0;
        while (start < left.Count && start < right.Count && string.Equals(leftKeys[start], rightKeys[start], StringComparison.Ordinal))
            start++;

        var endLeft = left.Count - 1;
        var endRight = right.Count - 1;
        while (endLeft >= start && endRight >= start && string.Equals(leftKeys[endLeft], rightKeys[endRight], StringComparison.Ordinal))
        {
            endLeft--;
            endRight--;
        }

        for (var index = 0; index < start; index++)
            ops.Add((' ', left[index]));

        var rows = endLeft - start + 1;
        var columns = endRight - start + 1;

        if (rows > 0 || columns > 0)
        {
            // One side can be empty after the trim (an empty or unparseable document): every line of the
            // other side is then a plain addition/removal, not something to align.
            if (rows == 0 || columns == 0)
            {
                for (var index = start; index <= endLeft; index++) ops.Add(('-', left[index]));
                for (var index = start; index <= endRight; index++) ops.Add(('+', right[index]));
            }
            else if ((long)rows * columns > MaxAlignCells)
            {
                tooLarge = true;
                for (var index = start; index <= endLeft; index++) ops.Add(('-', left[index]));
                for (var index = start; index <= endRight; index++) ops.Add(('+', right[index]));
            }
            else
            {
                var length = new int[rows + 1, columns + 1];
                for (var i = rows - 1; i >= 0; i--)
                {
                    for (var j = columns - 1; j >= 0; j--)
                    {
                        length[i, j] = string.Equals(leftKeys[start + i], rightKeys[start + j], StringComparison.Ordinal)
                            ? length[i + 1, j + 1] + 1
                            : Math.Max(length[i + 1, j], length[i, j + 1]);
                    }
                }

                var x = 0;
                var y = 0;
                while (x < rows && y < columns)
                {
                    if (string.Equals(leftKeys[start + x], rightKeys[start + y], StringComparison.Ordinal))
                    {
                        ops.Add((' ', left[start + x]));
                        x++;
                        y++;
                    }
                    else if (length[x + 1, y] >= length[x, y + 1])
                    {
                        ops.Add(('-', left[start + x]));
                        x++;
                    }
                    else
                    {
                        ops.Add(('+', right[start + y]));
                        y++;
                    }
                }

                while (x < rows) ops.Add(('-', left[start + x++]));
                while (y < columns) ops.Add(('+', right[start + y++]));
            }
        }

        for (var index = endLeft + 1; index < left.Count; index++)
            ops.Add((' ', left[index]));

        return ops;
    }

    /// <summary>Writes the alignment, collapsing long runs of unchanged lines so a small difference stays readable.</summary>
    private static void WriteDiff(StringBuilder text, List<(char Kind, string Line)> ops)
    {
        const int Context = 3;
        var index = 0;
        while (index < ops.Count)
        {
            if (ops[index].Kind != ' ')
            {
                text.Append(ops[index].Kind).Append(' ').Append(ops[index].Line).Append('\n');
                index++;
                continue;
            }

            var runEnd = index;
            while (runEnd < ops.Count && ops[runEnd].Kind == ' ') runEnd++;
            var run = runEnd - index;

            if (run <= Context * 2 && index > 0 && runEnd < ops.Count)
            {
                for (var i = index; i < runEnd; i++) text.Append("  ").Append(ops[i].Line).Append('\n');
            }
            else
            {
                var head = index == 0 ? 0 : Context;
                var tail = runEnd == ops.Count ? 0 : Context;
                for (var i = index; i < index + head; i++) text.Append("  ").Append(ops[i].Line).Append('\n');
                if (run - head - tail > 0)
                    text.Append("  … ").Append(run - head - tail).Append(" unchanged line(s) …\n");
                for (var i = runEnd - tail; i < runEnd; i++) text.Append("  ").Append(ops[i].Line).Append('\n');
            }

            index = runEnd;
        }
    }
}
