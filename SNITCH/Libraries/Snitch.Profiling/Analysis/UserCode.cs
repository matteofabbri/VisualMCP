namespace Snitch.Profiling.Analysis;

/// <summary>
/// Shared "is this code the user can act on" logic. Sources are "path:line", so the line is
/// stripped before any path comparison. The solution directory is preferred; a source that is
/// neither the .NET runtime nor the NuGet cache still counts as user code, which keeps hot-path and
/// I/O reports useful when the profiled process was published outside the MCP solution.
/// </summary>
internal static class UserCode
{
    public static string? SourceFile(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        var text = source;
        var colon = text.LastIndexOf(':');
        if (colon > 1 && int.TryParse(text[(colon + 1)..], out _))
            text = text[..colon];
        return text;
    }

    public static bool IsUnderRoot(string? source, string? userCodeRoot)
    {
        if (string.IsNullOrWhiteSpace(userCodeRoot))
            return false;

        var file = SourceFile(source);
        if (file is null)
            return false;

        try
        {
            var full = Path.GetFullPath(file);
            var root = Path.GetFullPath(userCodeRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return file.StartsWith(userCodeRoot, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsNonFramework(string? source)
    {
        var file = SourceFile(source);
        if (file is null)
            return false;

        try
        {
            var full = Path.GetFullPath(file);
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDirectory is not null && full.StartsWith(runtimeDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            var nuget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            if (full.StartsWith(nuget, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        catch
        {
            return true;
        }
    }

    public static bool IsUser(string? source, string? userCodeRoot) =>
        IsUnderRoot(source, userCodeRoot) || IsNonFramework(source);

    public static bool IsLikelyUserMethod(string method) =>
        !IsSyntheticFrame(method) &&
        !method.StartsWith("System.", StringComparison.Ordinal) &&
        !method.StartsWith("Microsoft.", StringComparison.Ordinal) &&
        !method.StartsWith("Interop", StringComparison.Ordinal) &&
        !method.StartsWith("Internal.", StringComparison.Ordinal) &&
        !method.StartsWith("Windows.", StringComparison.Ordinal);

    public static bool IsSyntheticFrame(string method) =>
        method is "(Non-Activities)" or "BROKEN" or "Thread" or "Threads" ||
        method.StartsWith("Process64 ", StringComparison.Ordinal) ||
        method.StartsWith("Process32 ", StringComparison.Ordinal) ||
        method.StartsWith("Thread ", StringComparison.Ordinal);

    /// <summary>The deepest frame (leaf-first input) with a source the user can open.</summary>
    public static TraceFrameInfo? DeepestUserFrame(
        IReadOnlyList<TraceFrameInfo> frames, string? userCodeRoot)
    {
        var underRoot = frames.FirstOrDefault(f => IsUnderRoot(f.Source, userCodeRoot));
        if (underRoot is not null)
            return underRoot;

        var nonFramework = frames.FirstOrDefault(f => IsNonFramework(f.Source));
        if (nonFramework is not null)
            return nonFramework;

        if (frames.All(f => string.IsNullOrWhiteSpace(f.Source)))
            return frames.FirstOrDefault(f => IsLikelyUserMethod(f.Method));

        return null;
    }
}
