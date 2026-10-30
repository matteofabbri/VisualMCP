namespace Snitch.Workspace;

/// <summary>
/// Recursively enumerates files matching a search pattern beneath a root, pruning
/// build-output / VCS / package noise directories and never throwing on inaccessible
/// folders. The shared engine behind <see cref="SolutionDiscovery"/> and the context's
/// Markdown-doc scan, so both behave identically.
/// </summary>
internal static class FileDiscovery
{
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", ".git", ".vs", "node_modules", "packages"];

    /// <summary>
    /// Yields the paths of files matching <paramref name="searchPattern"/> beneath
    /// <paramref name="root"/> (as returned by <see cref="Directory.GetFiles(string,string)"/>),
    /// skipping noise directories and any folder that cannot be accessed.
    /// </summary>
    public static IEnumerable<string> Walk(string root, string searchPattern, int maxDepth = 24)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return [];
        return WalkCore(root, searchPattern, depth: 0, maxDepth);
    }

    private static IEnumerable<string> WalkCore(string dir, string searchPattern, int depth, int maxDepth)
    {
        if (depth > maxDepth) yield break;

        string[] files;
        try { files = Directory.GetFiles(dir, searchPattern); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }

        foreach (var f in files) yield return f;

        string[] subDirs;
        try { subDirs = Directory.GetDirectories(dir); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }

        foreach (var sub in subDirs)
        {
            if (SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var f in WalkCore(sub, searchPattern, depth + 1, maxDepth))
                yield return f;
        }
    }
}
