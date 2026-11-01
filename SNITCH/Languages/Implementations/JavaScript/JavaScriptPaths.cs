namespace Snitch.Languages.JavaScript;

/// <summary>
/// The path rules every JavaScript tool shares: which extensions a script is written with, where a
/// relative specifier points, and which folders are generated rather than source.
/// </summary>
internal static class JavaScriptPaths
{
    private static readonly string[] ExcludedDirectories =
    [
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "dist", "build", "out",
        "coverage", ".next", ".nuxt", ".svelte-kit", "target", "vendor",
    ];

    /// <summary>The source extensions a relative import may omit, in the order TypeScript resolves them.</summary>
    private static readonly string[] ResolutionSuffixes =
        [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    /// <summary>
    /// The file a relative specifier points at: the path itself, the path with a source extension, a
    /// directory index, or the TypeScript source behind a compiled <c>.js</c> specifier. Null when none exists.
    /// </summary>
    public static string? ResolveRelative(string fromFile, string specifier)
    {
        var directory = Path.GetDirectoryName(fromFile);
        if (directory is null) return null;

        var target = Path.GetFullPath(Path.Combine(directory, specifier.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(target)) return target;

        // A TypeScript project imports './x.js' although the source on disk is './x.ts'.
        if (target.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            target.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.ChangeExtension(target, null);
            foreach (var suffix in ResolutionSuffixes)
                if (File.Exists(stem + suffix))
                    return stem + suffix;
        }

        foreach (var suffix in ResolutionSuffixes)
            if (File.Exists(target + suffix))
                return target + suffix;

        foreach (var suffix in ResolutionSuffixes)
        {
            var index = Path.Combine(target, "index" + suffix);
            if (File.Exists(index)) return index;
        }

        return null;
    }

    /// <summary>The path relative to the scan root, with forward slashes, which is how answers are keyed.</summary>
    public static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    public static string SnippetAt(string text, int line)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (line < 1 || line > lines.Length) return string.Empty;
        var snippet = lines[line - 1].Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
    }

    /// <summary>Every script under the root, in a stable order, with the generated folders pruned.</summary>
    public static (string Root, List<string> Files, bool Truncated) Discover(string root, int maxFiles)
    {
        var files = new List<string>();
        var truncated = false;

        if (File.Exists(root))
        {
            files.Add(root);
            return (Path.GetDirectoryName(root) ?? root, files, false);
        }

        if (!Directory.Exists(root)) return (root, files, false);

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".artifacts", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            string[] entries;
            try { entries = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (!JavaScriptAnalyzer.IsScript(entry)) continue;
                if (files.Count >= maxFiles) { truncated = true; break; }
                files.Add(entry);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (root, files, truncated);
    }
}
