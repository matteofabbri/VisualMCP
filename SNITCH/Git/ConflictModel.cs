namespace Snitch.Git;

/// <summary>
/// One conflict region of a file: the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> block, the optional
/// <c>|||||||</c> base that <c>merge.conflictStyle=diff3</c> adds, and the <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>
/// side. Line numbers are 1-based and point at the markers in the file as it stands on disk.
/// </summary>
public sealed record ConflictHunk(
    int Index,
    int StartLine,
    int EndLine,
    string? OursLabel,
    string? BaseLabel,
    string? TheirsLabel,
    IReadOnlyList<string> Ours,
    IReadOnlyList<string> Base,
    IReadOnlyList<string> Theirs,
    bool HasBase)
{
    public int OursLineCount => Ours.Count;
    public int TheirsLineCount => Theirs.Count;
    public int BaseLineCount => Base.Count;
}

/// <summary>The conflict regions found in one file.</summary>
public sealed record ParsedConflictFile(string Path, bool Binary, bool TooLarge, int LineCount, IReadOnlyList<ConflictHunk> Hunks);

/// <summary>
/// Reads the conflict markers git leaves in a working-tree file and rewrites a chosen side over
/// them, which is what a merge editor does — as data, so a client can render it.
/// </summary>
public static class ConflictModel
{
    /// <summary>Files larger than this are reported without being parsed (a conflict in a generated file is not worth the read).</summary>
    public const int MaxParseBytes = 4 * 1024 * 1024;

    /// <summary>How a hunk is resolved. <c>both</c> keeps our lines then their lines.</summary>
    public const string Ours = "ours";
    public const string Theirs = "theirs";
    public const string Both = "both";
    public const string Base = "base";
    public const string Custom = "custom";

    public static bool IsStrategy(string value) =>
        value is Ours or Theirs or Both or Base or Custom;

    /// <summary>
    /// Parses <paramref name="text"/> into its conflict hunks. <paramref name="newLine"/> is the
    /// terminator the file uses (kept so a rewrite can put the file back together unchanged).
    /// </summary>
    public static (IReadOnlyList<ConflictHunk> Hunks, string[] Lines, string NewLine) Parse(string text)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var hunks = new List<ConflictHunk>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryMarker(lines[i], '<', out var oursLabel)) continue;

            var start = i;
            var ours = new List<string>();
            var baseLines = new List<string>();
            var theirs = new List<string>();
            var baseLabel = (string?)null;
            var theirsLabel = (string?)null;
            var hasBase = false;
            var inOurs = true;
            var inBase = false;
            var closed = false;

            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (TryMarker(lines[j], '|', out var b) && inOurs)
                {
                    hasBase = true;
                    baseLabel = b;
                    inOurs = false;
                    inBase = true;
                    continue;
                }

                if (IsSeparator(lines[j]) && (inOurs || inBase))
                {
                    inOurs = false;
                    inBase = false;
                    continue;
                }

                if (TryMarker(lines[j], '>', out var t))
                {
                    theirsLabel = t;
                    closed = true;
                    break;
                }

                if (inOurs) ours.Add(lines[j]);
                else if (inBase) baseLines.Add(lines[j]);
                else theirs.Add(lines[j]);
            }

            if (!closed) break;   // an unterminated block is not a conflict git wrote

            hunks.Add(new ConflictHunk(
                hunks.Count, start + 1, j + 1, oursLabel, baseLabel, theirsLabel,
                ours, baseLines, theirs, hasBase));

            i = j;
        }

        return (hunks, lines, newLine);
    }

    /// <summary>
    /// Rewrites <paramref name="text"/> with <paramref name="hunks"/> resolved: one hunk when
    /// <paramref name="hunkIndex"/> is given, every hunk otherwise. Lines are addressed from the
    /// parse, so the caller must not have edited the file in between.
    /// </summary>
    public static (string Text, int Resolved, int Remaining) Resolve(
        string text, IReadOnlyList<ConflictHunk> hunks, string strategy,
        int? hunkIndex, string? customContent)
    {
        var (_, lines, newLine) = Parse(text);

        var targets = hunkIndex is { } index
            ? hunks.Where(h => h.Index == index).ToList()
            : hunks.ToList();

        var resolved = 0;
        // Bottom-up so the earlier line numbers stay valid.
        foreach (var hunk in targets.OrderByDescending(h => h.StartLine))
        {
            var replacement = strategy switch
            {
                Ours => hunk.Ours.ToList(),
                Theirs => hunk.Theirs.ToList(),
                Both => hunk.Ours.Concat(hunk.Theirs).ToList(),
                Base => hunk.Base.ToList(),
                Custom => (customContent ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown conflict strategy."),
            };

            // Keep the file's final-newline habit: a custom block that ends with a newline
            // contributes an empty last element, which would otherwise become a blank line.
            if (strategy == Custom && replacement.Count > 1 && replacement[^1].Length == 0)
                replacement = replacement.GetRange(0, replacement.Count - 1);

            var startIndex = hunk.StartLine - 1;
            var endIndex = hunk.EndLine - 1;               // inclusive marker line
            var count = endIndex - startIndex + 1;

            var updated = new List<string>(lines.Length - count + replacement.Count);
            updated.AddRange(lines.Take(startIndex));
            updated.AddRange(replacement);
            updated.AddRange(lines.Skip(startIndex + count));

            lines = updated.ToArray();
            resolved++;
        }

        var rebuilt = string.Join(newLine, lines);
        var remaining = Parse(rebuilt).Hunks.Count;
        return (rebuilt, resolved, remaining);
    }

    /// <summary>Full text of one hunk as a diff3 block — what a merge editor shows for it.</summary>
    public static string Render(ConflictHunk hunk, string newLine = "\n")
    {
        var lines = new List<string> { $"<<<<<<< {hunk.OursLabel ?? "ours"}".TrimEnd() };
        lines.AddRange(hunk.Ours);
        if (hunk.HasBase)
        {
            lines.Add($"||||||| {hunk.BaseLabel ?? "base"}".TrimEnd());
            lines.AddRange(hunk.Base);
        }
        lines.Add("=======");
        lines.AddRange(hunk.Theirs);
        lines.Add($">>>>>>> {hunk.TheirsLabel ?? "theirs"}".TrimEnd());
        return string.Join(newLine, lines);
    }

    private static bool TryMarker(string line, char marker, out string? label)
    {
        label = null;
        var trimmed = line.TrimStart();
        if (trimmed.Length < 7) return false;

        for (var k = 0; k < 7; k++)
            if (trimmed[k] != marker) return false;

        // The part after the seven marker characters: nothing, or a space and a label.
        var suffix = trimmed[7..];
        if (suffix.Length > 0 && suffix[0] != ' ') return false;

        label = suffix.Trim().Length == 0 ? null : suffix.Trim();
        return true;
    }

    private static bool IsSeparator(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length != 7) return false;
        for (var k = 0; k < 7; k++)
            if (trimmed[k] != '=') return false;
        return true;
    }
}
