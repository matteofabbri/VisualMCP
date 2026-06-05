using System.Text.RegularExpressions;

namespace VisualMCP.Implementation.Search;

internal static class SearchTextImpl
{
    private static readonly string[] NoiseSegments =
    {
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
    };

    private const long MaxFileBytes = 10 * 1024 * 1024;

    internal static object Run(string pattern, string path, string glob, bool recursive, bool ignoreCase, int maxMatches, int contextLines)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return new { error = "A search pattern (regex) is required." };
        if (string.IsNullOrWhiteSpace(path)) return new { error = "A path (file or directory) is required." };

        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)); }
        catch (Exception ex) { return new { error = $"Invalid regex '{pattern}': {ex.Message}" }; }

        if (maxMatches < 1) maxMatches = 1;
        if (maxMatches > 5000) maxMatches = 5000;
        if (contextLines < 0) contextLines = 0;
        if (contextLines > 10) contextLines = 10;

        IEnumerable<string> files;
        if (File.Exists(path))
            files = new[] { Path.GetFullPath(path) };
        else if (Directory.Exists(path))
        {
            try
            {
                files = Directory.EnumerateFiles(path, string.IsNullOrWhiteSpace(glob) ? "*" : glob,
                    new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, MatchType = MatchType.Simple })
                    .Where(f => !NoiseSegments.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) { return new { error = $"Failed to enumerate '{path}': {ex.Message}" }; }
        }
        else return new { error = $"Path not found: {path}" };

        var matches = new List<object>();
        var filesScanned = 0;
        var filesWithMatches = 0;
        var truncated = false;

        foreach (var file in files)
        {
            if (matches.Count >= maxMatches) { truncated = true; break; }

            string[] lines;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > MaxFileBytes) continue;
                lines = File.ReadAllLines(file);
            }
            catch { continue; }

            filesScanned++;
            var hadMatch = false;

            for (var i = 0; i < lines.Length; i++)
            {
                if (!rx.IsMatch(lines[i])) continue;
                hadMatch = true;
                if (matches.Count >= maxMatches) { truncated = true; break; }

                object? context = null;
                if (contextLines > 0)
                {
                    var from = Math.Max(0, i - contextLines);
                    var to   = Math.Min(lines.Length - 1, i + contextLines);
                    context = Enumerable.Range(from, to - from + 1)
                        .Select(n => new { line = n + 1, text = Truncate(lines[n]) }).ToList();
                }

                matches.Add(new
                {
                    file,
                    line = i + 1,
                    text = Truncate(lines[i]),
                    context,
                });
            }

            if (hadMatch) filesWithMatches++;
            if (truncated) break;
        }

        return new
        {
            pattern, path, glob = string.IsNullOrWhiteSpace(glob) ? "*" : glob, recursive, ignoreCase,
            filesScanned, filesWithMatches, matchCount = matches.Count, truncated, matches,
        };
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
