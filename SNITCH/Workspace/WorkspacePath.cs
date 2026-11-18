namespace Snitch.Workspace;

/// <summary>
/// Path arithmetic for the workspace boundary: whether a path belongs to the directory a server was
/// started on. One VisualMCP process serves one workspace, so this is what a solution outside that
/// directory is measured against.
/// </summary>
public static class WorkspacePath
{
    /// <summary>
    /// Windows paths are case-insensitive; on Unix a different case is a different directory.
    /// </summary>
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> itself or lives beneath it,
    /// with the platform's own case rule.
    /// </summary>
    public static bool IsWithin(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

        string fullRoot, fullPath;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (fullPath.Equals(fullRoot, Comparison)) return true;

        // A root that is itself a drive/filesystem root ("C:\", "/") already ends with a separator:
        // appending another one would turn "C:\" into "C:\\" and match nothing.
        var prefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, Comparison)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, Comparison);
    }
}
