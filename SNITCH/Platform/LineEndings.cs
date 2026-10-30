namespace Snitch.Platform;

/// <summary>Which newline a text file uses.</summary>
public enum NewLineStyle
{
    Lf,
    CrLf,
}

/// <summary>
/// Line-ending policy: read a file's own style, learn a repository's style from .editorconfig and
/// .gitattributes, and normalise text to one of the two. The rule everywhere is "never change a file's
/// convention silently": a file written by a refactoring keeps the endings it already had, and only
/// <c>fix_line_endings</c> migrates on purpose.
/// </summary>
public static class LineEndings
{
    public const string Lf = "\n";
    public const string CrLf = "\r\n";

    private const int MaxAncestorDepth = 6;

    private static readonly string[] SiblingExtensions =
        [".cs", ".csproj", ".sln", ".slnx", ".md", ".json", ".yml", ".yaml", ".props", ".targets", ".txt"];

    /// <summary>How many CRLF and how many bare LF sequences the text contains.</summary>
    public static (int CrLfCount, int LfCount) Count(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);

        var crlf = 0;
        var lf = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n') continue;

            if (index > 0 && text[index - 1] == '\r') crlf++;
            else lf++;
        }

        return (crlf, lf);
    }

    public static bool HasMixed(string? text)
    {
        var (crlf, lf) = Count(text);
        return crlf > 0 && lf > 0;
    }

    public static string For(NewLineStyle style) => style == NewLineStyle.CrLf ? CrLf : Lf;

    /// <summary>The style of the text, falling back to <paramref name="fallback"/> when it has no newline.</summary>
    public static NewLineStyle Detect(string? text, NewLineStyle fallback)
    {
        var (crlf, lf) = Count(text);
        if (crlf == 0 && lf == 0) return fallback;
        if (crlf == lf) return fallback;

        return crlf > lf ? NewLineStyle.CrLf : NewLineStyle.Lf;
    }

    /// <summary>Rewrites every newline (including lone CR) to the requested style.</summary>
    public static string Normalize(string text, NewLineStyle style)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var newline = For(style);
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        return newline == Lf ? normalized : normalized.Replace("\n", newline);
    }

    /// <summary>
    /// The style to write <paramref name="path"/> with: an existing file keeps its own convention
    /// (a whole-file rewrite would otherwise turn one edited line into a full-file diff), a new file
    /// follows the repository first and the machine second.
    /// </summary>
    public static NewLineStyle ForFile(string path)
    {
        if (File.Exists(path))
        {
            var text = ReadOrNull(path);
            var (crlf, lf) = Count(text);
            if (crlf != lf) return crlf > lf ? NewLineStyle.CrLf : NewLineStyle.Lf;
            if (crlf > 0) return DefaultStyle;
        }

        return ForNewFile(path);
    }

    /// <summary>
    /// For a file that does not exist yet: .editorconfig, then .gitattributes, then the files already
    /// beside it, then the machine's own convention.
    /// </summary>
    public static NewLineStyle ForNewFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (FromEditorConfig(directory, path) is { } fromEditorConfig) return fromEditorConfig;
        if (FromGitAttributes(directory, path) is { } fromGitAttributes) return fromGitAttributes;
        if (FromSiblings(directory) is { } fromSiblings) return fromSiblings;

        return DefaultStyle;
    }

    public static NewLineStyle DefaultStyle => HostSystem.IsWindows ? NewLineStyle.CrLf : NewLineStyle.Lf;

    public static string Describe(NewLineStyle style) => style == NewLineStyle.CrLf ? "crlf" : "lf";

    public static bool TryParseStyle(string? text, out NewLineStyle style)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "crlf" or "cr-lf" or "windows":
                style = NewLineStyle.CrLf;
                return true;
            case "lf" or "unix":
                style = NewLineStyle.Lf;
                return true;
            default:
                style = DefaultStyle;
                return false;
        }
    }

    // ── repository declarations ─────────────────────────────────────────────
    private static NewLineStyle? FromEditorConfig(string? directory, string filePath)
    {
        foreach (var configPath in AncestorFiles(directory, ".editorconfig"))
        {
            NewLineStyle? result = null;
            var applies = true;
            var isRoot = false;

            foreach (var raw in SafeReadLines(configPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    applies = MatchesPattern(line[1..^1], filePath, configPath);
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim();

                // `root = true` sits outside any section and means "stop looking further up", not
                // "stop reading this file" — it is usually the very first line.
                if (key.Equals("root", StringComparison.OrdinalIgnoreCase))
                {
                    isRoot = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!applies) continue;

                if (key.Equals("end_of_line", StringComparison.OrdinalIgnoreCase) &&
                    TryParseStyle(value, out var style))
                {
                    result = style; // the last matching section wins
                }
            }

            if (result is not null) return result;
            if (isRoot) return null;
        }

        return null;
    }

    private static NewLineStyle? FromGitAttributes(string? directory, string filePath)
    {
        foreach (var attributesPath in AncestorFiles(directory, ".gitattributes"))
        {
            NewLineStyle? result = null;

            foreach (var raw in SafeReadLines(attributesPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (!MatchesPattern(line.Split(' ', '\t')[0], filePath, attributesPath)) continue;

                if (line.Contains("eol=crlf", StringComparison.OrdinalIgnoreCase)) result = NewLineStyle.CrLf;
                else if (line.Contains("eol=lf", StringComparison.OrdinalIgnoreCase)) result = NewLineStyle.Lf;
            }

            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>
    /// What the other files in the folder use. In a repository with neither .editorconfig nor
    /// .gitattributes this is the best signal there is, and it is what keeps a new file consistent with
    /// the checkout git produced.
    /// </summary>
    private static NewLineStyle? FromSiblings(string? directory)
    {
        var current = directory;
        for (var depth = 0; depth < 3 && current is not null; depth++)
        {
            var crlf = 0;
            var lf = 0;

            try
            {
                foreach (var file in Directory.EnumerateFiles(current)
                             .Where(file => SiblingExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                             .Take(20))
                {
                    var text = ReadOrNull(file);
                    var (fileCrLf, fileLf) = Count(text);
                    if (fileCrLf == fileLf) continue;

                    if (fileCrLf > fileLf) crlf++;
                    else lf++;
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (crlf != lf) return crlf > lf ? NewLineStyle.CrLf : NewLineStyle.Lf;
            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    // ── plumbing ────────────────────────────────────────────────────────────
    private static IEnumerable<string> AncestorFiles(string? directory, string fileName)
    {
        var current = directory;
        for (var depth = 0; depth < MaxAncestorDepth && current is not null; depth++)
        {
            var candidate = Path.Combine(current, fileName);
            if (File.Exists(candidate)) yield return candidate;

            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    /// Matches the part of an .editorconfig/.gitattributes pattern that matters here: "*", "*.ext",
    /// "dir/*.ext" and exact names. Anything more elaborate falls back to "does not match", which is
    /// the safe direction: the file keeps its own or the machine's convention.
    /// </summary>
    private static bool MatchesPattern(string pattern, string filePath, string declarationPath)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;

        // .editorconfig patterns are relative to the declaring file's directory.
        var directory = Path.GetDirectoryName(declarationPath);
        var relative = directory is null
            ? Path.GetFileName(filePath)
            : Path.GetRelativePath(directory, filePath).Replace('\\', '/');

        var name = Path.GetFileName(filePath);

        if (pattern == "*" || pattern == "**") return true;
        if (!pattern.Contains('/') && !pattern.Contains('*')) return pattern.Equals(name, StringComparison.OrdinalIgnoreCase);

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);

        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var folder = pattern[..^2];
            return relative.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith("**/", StringComparison.Ordinal))
            return MatchesPattern(pattern[3..], filePath, declarationPath);

        return string.Equals(relative, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
