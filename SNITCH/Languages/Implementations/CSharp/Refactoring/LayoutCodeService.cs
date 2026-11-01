using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// CodeMaid's "Format Document" rules that the Roslyn formatter does not implement: reflowing a
/// block of <c>//</c> comments to a width, joining the lines of one statement, and normalising blank
/// lines (no runs of them, none after an opening brace or before a closing one, one newline at EOF).
/// <para>
/// The rules are text-level, but each edit is guarded the way the refactorings are: a join is accepted
/// only when the token stream is unchanged (so a verbatim string or a comment cannot be swallowed), and
/// the file is refused when the projected text would add a compiler error. Comments and blank lines
/// apply to C# and Visual Basic; joining lines is C# only because the token check is C#'s parser.
/// </para>
/// </summary>
public interface ILayoutCodeService
{
    Task<object> Apply(
        string scope,
        string? filePath,
        string? projectName,
        string? rules,
        int maxCommentWidth,
        int? fromLine,
        int? toLine,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class LayoutCodeService : ILayoutCodeService
{
    private static readonly string[] SupportedRules = ["comments", "join", "blank_lines"];

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public LayoutCodeService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> Apply(
        string scope,
        string? filePath,
        string? projectName,
        string? rules,
        int maxCommentWidth,
        int? fromLine,
        int? toLine,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("document" or "project" or "solution"))
            return new { error = "scope must be document, project or solution." };

        var requested = string.IsNullOrWhiteSpace(rules)
            ? new List<string> { "comments", "blank_lines" }
            : rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(rule => rule.ToLowerInvariant().Replace('-', '_'))
                   .Distinct()
                   .ToList();

        var unknown = requested.Except(SupportedRules, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            return new { error = $"Unknown layout rule(s): {string.Join(", ", unknown)}.", supported = SupportedRules };

        if (requested.Contains("join") && (fromLine is null || toLine is null || toLine < fromLine))
            return new
            {
                error = "The join rule joins one line range: pass fromLine and toLine.",
                supported = SupportedRules,
            };

        var width = Math.Clamp(maxCommentWidth <= 0 ? 120 : maxCommentWidth, 40, 400);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No solution could be auto-located from the working directory. Call load_solution first." };

        var (documents, selectionError) = DocumentScopes.Select(solution, level, filePath, projectName);
        if (selectionError is not null) return selectionError;

        var files = new List<string>();
        var refused = new List<object>();
        var rewrites = new List<object>();
        var commentsWrapped = 0;
        var blankLinesRemoved = 0;
        var linesJoined = 0;

        foreach (var document in documents)
        {
            var text = await document.GetTextAsync(cancellationToken);
            var original = text.ToString();
            var newLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = SplitLines(original);
            var protectedLines = await MultiLineTokenLinesAsync(document, cancellationToken);

            var edited = false;

            if (requested.Contains("join") && document.Project.Language == LanguageNames.CSharp)
            {
                var joined = TryJoin(lines, fromLine!.Value, toLine!.Value, newLine, out var joinError);
                if (joinError is not null)
                {
                    refused.Add(new { file = document.FilePath, rule = "join", reason = joinError });
                }
                else if (joined is not null)
                {
                    lines = joined.Value.Lines;
                    linesJoined += joined.Value.Joined;
                    edited = true;
                }
            }
            else if (requested.Contains("join") && document.Project.Language != LanguageNames.CSharp)
            {
                refused.Add(new { file = document.FilePath, rule = "join", reason = "Joining lines is C# only." });
            }

            if (requested.Contains("comments"))
            {
                var (wrapped, count) = WrapComments(lines, width, protectedLines);
                if (count > 0)
                {
                    lines = wrapped;
                    commentsWrapped += count;
                    edited = true;
                }
            }

            if (requested.Contains("blank_lines"))
            {
                var (normalised, removed) = NormalizeBlankLines(lines, protectedLines);
                if (removed > 0)
                {
                    lines = normalised;
                    blankLinesRemoved += removed;
                    edited = true;
                }
            }

            if (!edited) continue;

            var after = string.Join(newLine, lines);
            if (!after.EndsWith(newLine, StringComparison.Ordinal)) after += newLine;

            var afterSolution = solution.WithDocumentText(document.Id, SourceText.From(after, text.Encoding));
            var gate = await RefactoringCompilerGate.CompareAsync(
                solution, afterSolution, [document.Project.Id], cancellationToken);
            if (!gate.Passed)
            {
                refused.Add(RefactoringCompilerGate.Refusal(gate, $"laying out {Path.GetFileName(document.FilePath)}"));
                continue;
            }

            files.Add(document.FilePath ?? document.Name);
            rewrites.Add(new
            {
                file = document.FilePath,
                preview = RefactoringFileWriter.Diff(original, after),
            });

            if (dryRun) continue;

            _store?.RecordEdit("layout_code", document.FilePath ?? document.Name,
                string.Join(",", requested), original, after);
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, after, cancellationToken);
        }

        return new
        {
            tool = "layout_code",
            scope = level,
            rules = requested,
            maxCommentWidth = width,
            dryRun,
            applied = !dryRun,
            filesModified = files.Count,
            files,
            commentsWrapped,
            blankLinesRemoved,
            linesJoined,
            refused,
            previews = rewrites,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files."
                : "Files written; comments and blank lines apply to C# and Visual Basic, joining lines is C# only.",
        };
    }

    // ── comments ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reflows every block of full-line <c>//</c> comments (not XML docs, not a separator line of
    /// punctuation) to the width, keeping each paragraph and the block's indentation.
    /// </summary>
    private static (string[] Lines, int Wrapped) WrapComments(string[] lines, int width, HashSet<int> protectedLines)
    {
        var result = new List<string>(lines.Length);
        var wrapped = 0;
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();

            if (!trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("///", StringComparison.Ordinal) ||
                protectedLines.Contains(index))
            {
                result.Add(line);
                index++;
                continue;
            }

            var indentation = line[..(line.Length - trimmed.Length)];
            var block = new List<string>();
            var start = index;

            while (index < lines.Length && !protectedLines.Contains(index))
            {
                var candidate = lines[index];
                var candidateTrimmed = candidate.TrimStart();
                if (!candidateTrimmed.StartsWith("//", StringComparison.Ordinal) ||
                    candidateTrimmed.StartsWith("///", StringComparison.Ordinal)) break;

                var content = candidateTrimmed.Length > 2 ? candidateTrimmed[2..].Trim() : string.Empty;
                if (content.Length > 0 && content.All(character => !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character)))
                    break;

                if (candidate[..(candidate.Length - candidateTrimmed.Length)] != indentation) break;

                block.Add(content);
                index++;
            }

            if (block.Count == 0)
            {
                // A banner ('// ---------') is a drawing, not prose: leave it and everything after it alone.
                result.Add(lines[start]);
                index = start + 1;
                continue;
            }

            var paragraphs = new List<List<string>> { new() };
            foreach (var content in block)
            {
                if (content.Length == 0)
                {
                    if (paragraphs[^1].Count > 0) paragraphs.Add([]);
                    continue;
                }

                foreach (var word in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    paragraphs[^1].Add(word);
            }

            var rendered = new List<string>();
            foreach (var paragraph in paragraphs)
            {
                if (paragraph.Count == 0)
                {
                    if (rendered.Count > 0) rendered.Add(indentation + "//");
                    continue;
                }

                var current = new StringBuilder(indentation + "// ");
                var currentLength = current.Length;
                foreach (var word in paragraph)
                {
                    var addition = (currentLength > indentation.Length + 3 ? 1 : 0) + word.Length;
                    if (currentLength + addition > width && currentLength > indentation.Length + 3)
                    {
                        rendered.Add(current.ToString().TrimEnd());
                        current.Clear().Append(indentation).Append("// ").Append(word);
                        currentLength = current.Length;
                    }
                    else
                    {
                        if (currentLength > indentation.Length + 3) current.Append(' ');
                        current.Append(word);
                        currentLength = current.Length;
                    }
                }

                rendered.Add(current.ToString().TrimEnd());
            }

            if (rendered.Count != block.Count) wrapped += Math.Abs(rendered.Count - block.Count);
            result.AddRange(rendered);

            // The loop stopped on something that is not part of this block: let the outer loop look at it.
            if (index == start) index++;
        }

        return (result.ToArray(), wrapped);
    }

    // ── blank lines ─────────────────────────────────────────────────────────────

    /// <summary>
    /// No run of blank lines, no blank line after an opening brace or before a closing one, and one
    /// newline at end of file. A line that is part of a multi-line token (a verbatim or raw string) is
    /// never touched, and neither is the line next to it.
    /// </summary>
    private static (string[] Lines, int Removed) NormalizeBlankLines(string[] lines, HashSet<int> protectedLines)
    {
        var kept = new List<string>(lines.Length);
        var removed = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line) || protectedLines.Contains(index) || NearProtected(index, protectedLines))
            {
                kept.Add(line);
                continue;
            }

            var previous = kept.Count > 0 ? kept[^1].TrimEnd() : null;
            var next = index + 1 < lines.Length ? lines[index + 1] : null;

            // A run: only the first blank line of it survives.
            if (previous is not null && previous.Length == 0)
            {
                removed++;
                continue;
            }

            // No blank line right after an opening brace, or right before a closing one.
            if (previous is not null && previous.EndsWith("{", StringComparison.Ordinal) &&
                !previous.Contains("//", StringComparison.Ordinal))
            {
                removed++;
                continue;
            }

            if (next is not null && next.TrimStart().StartsWith("}", StringComparison.Ordinal))
            {
                removed++;
                continue;
            }

            kept.Add(line);
        }

        // Trailing blank lines go; there is exactly one final newline when the file has any content.
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[^1])) kept.RemoveAt(kept.Count - 1);
        if (kept.Count == 0) kept.Add(string.Empty);

        return (kept.ToArray(), removed);
    }

    private static bool NearProtected(int index, HashSet<int> protectedLines) =>
        protectedLines.Contains(index - 1) || protectedLines.Contains(index + 1);

    // ── join ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Joins a line range into one statement, accepted only when the token stream is unchanged: a
    /// verbatim string, a comment or a preprocessor directive would change meaning, so it is refused.
    /// </summary>
    private static (string[] Lines, int Joined)? TryJoin(
        string[] lines, int fromLine, int toLine, string newLine, out string? error)
    {
        error = null;
        if (fromLine < 1 || toLine > lines.Length || toLine <= fromLine)
        {
            error = $"The range must be two or more existing lines (the file has {lines.Length}).";
            return null;
        }

        var range = lines[(fromLine - 1)..toLine];
        if (range.Any(line => line.Contains("//", StringComparison.Ordinal) ||
                              line.Contains("/*", StringComparison.Ordinal) ||
                              line.TrimStart().StartsWith("#", StringComparison.Ordinal)))
        {
            error = "The range contains a comment or a directive; joining it would swallow the rest of the line.";
            return null;
        }

        var indentation = range[0][..(range[0].Length - range[0].TrimStart().Length)];
        var originalFragment = string.Join(newLine, range);
        var joinedLine = indentation + string.Join(" ", range.Select(line => line.Trim()));

        var before = SyntaxFactory.ParseTokens(originalFragment).Select(token => (token.RawKind, token.Text)).ToList();
        var after = SyntaxFactory.ParseTokens(joinedLine).Select(token => (token.RawKind, token.Text)).ToList();
        if (!before.SequenceEqual(after))
        {
            error = "Joining the range would change its tokens (a multi-line literal or an ambiguous split).";
            return null;
        }

        var result = new List<string>(lines.Length);
        result.AddRange(lines[..(fromLine - 1)]);
        result.Add(joinedLine);
        result.AddRange(lines[toLine..]);
        return (result.ToArray(), range.Length - 1);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.ToArray();
    }

    /// <summary>The 0-based line indexes covered by a token that spans lines (a verbatim or raw string).</summary>
    private static async Task<HashSet<int>> MultiLineTokenLinesAsync(Document document, CancellationToken cancellationToken)
    {
        var protectedLines = new HashSet<int>();
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root is null) return protectedLines;

        foreach (var token in root.DescendantTokens())
        {
            if (!token.Text.Contains('\n')) continue;

            var start = text.Lines.GetLineFromPosition(token.SpanStart).LineNumber;
            var end = text.Lines.GetLineFromPosition(Math.Max(token.SpanStart, token.Span.End - 1)).LineNumber;
            for (var line = start; line <= end; line++) protectedLines.Add(line);
        }

        return protectedLines;
    }
}
