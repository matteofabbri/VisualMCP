namespace VisualMCP.Workspace;

public sealed record ProjectDoc(
    string RelativePath,
    string FullPath,
    long SizeBytes,
    string? Content,
    bool ContentOmitted);

/// <summary>
/// Scans the solution directory for Markdown / documentation files and caches
/// their content in process, so the agent has the project's docs available
/// immediately after the solution opens.
/// </summary>
public static class ProjectDocsService
{
    private const long MaxFileBytesForContent = 512 * 1024;
    private const int  MaxPerFileChars        = 64 * 1024;
    private const int  MaxFiles               = 400;

    private static readonly string[] NoiseSegments =
    {
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}",
    };

    private static readonly HashSet<string> DocTxtNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "CHANGELOG", "CONTRIBUTING", "NOTES", "TODO", "ARCHITECTURE", "AUTHORS", "ROADMAP",
    };

    private static readonly object Gate = new();
    private static string? _root;
    private static IReadOnlyList<ProjectDoc>? _cache;

    /// <summary>Indexes the docs on a background thread; safe to call on every solution load.</summary>
    public static void WarmInBackground(string? solutionDir)
    {
        if (string.IsNullOrEmpty(solutionDir)) return;
        _ = Task.Run(() => { try { Index(solutionDir); } catch { /* best-effort */ } });
    }

    /// <summary>Returns the cached docs for the given root, indexing on demand if needed.</summary>
    public static (string root, IReadOnlyList<ProjectDoc> docs) Get(string solutionDir)
    {
        lock (Gate)
        {
            if (_cache is not null && string.Equals(_root, solutionDir, StringComparison.OrdinalIgnoreCase))
                return (_root!, _cache);
        }
        return (solutionDir, Index(solutionDir));
    }

    private static IReadOnlyList<ProjectDoc> Index(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        bool IsNoise(string p) => NoiseSegments.Any(s => p.Contains(s, StringComparison.OrdinalIgnoreCase));

        var paths = new List<string>();
        foreach (var pattern in new[] { "*.md", "*.markdown", "*.mdx" })
        {
            try { paths.AddRange(Directory.EnumerateFiles(root, pattern, opts).Where(f => !IsNoise(f))); }
            catch { /* best-effort */ }
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.txt", opts))
                if (!IsNoise(f) && DocTxtNames.Contains(Path.GetFileNameWithoutExtension(f)))
                    paths.Add(f);
        }
        catch { /* best-effort */ }

        var docs = new List<ProjectDoc>();
        foreach (var f in paths.Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                               .Take(MaxFiles))
        {
            try
            {
                var fi = new FileInfo(f);
                string? content = null;
                var omitted = false;
                if (fi.Length <= MaxFileBytesForContent)
                {
                    var text = File.ReadAllText(f);
                    content = text.Length > MaxPerFileChars ? text[..MaxPerFileChars] + "\n…(truncated)" : text;
                }
                else omitted = true;

                docs.Add(new ProjectDoc(Path.GetRelativePath(root, f), f, fi.Length, content, omitted));
            }
            catch { /* skip unreadable file */ }
        }

        lock (Gate) { _cache = docs; _root = root; }
        return docs;
    }
}
