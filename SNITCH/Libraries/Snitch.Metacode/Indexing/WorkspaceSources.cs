namespace Snitch.Metacode.Indexing;

/// <summary>
/// The files a solution is made of, as seen from disk: every TEXT file under a root, with the build,
/// tool and VCS folders skipped. This is what lets the metacode store hold a <c>.csproj</c>, a README or
/// a JSON config next to the code, instead of only the Compile items the workspace happens to know.
/// </summary>
/// <remarks>
/// The set is deliberately the one the full-text index uses (<c>FullTextIndexService</c>), so "what is in
/// the solution" means the same thing in both stores. The file's own bytes decide what is text: a NUL in
/// the first few kilobytes marks a binary, whatever its extension says, and anything over
/// <see cref="MaxFileBytes"/> is left out because the store is a code model, not an archive.
/// </remarks>
internal static class WorkspaceSources
{
    /// <summary>Bigger than any hand-written source; a log or a dump is not what this store is for.</summary>
    public const long MaxFileBytes = 1_000_000;

    /// <summary>The bytes read to decide whether a file is text.</summary>
    private const int SniffBytes = 8192;

    /// <summary>Directory names that never hold the solution's own content.</summary>
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "TestResults", "artifacts", "packages", "dist", "out", "target",
    };

    /// <summary>
    /// Whether a directory is skipped by the walk. Dot-folders are tool state (.git, .vs, .idea,
    /// .visualMCP, .artifacts-*), never the solution's content.
    /// </summary>
    public static bool IsNoise(string name) =>
        name.Length > 0 && (name[0] == '.' || NoiseDirectories.Contains(name));

    /// <summary>
    /// <summary>
    /// Every file under <paramref name="root"/>, recursively, in a stable order (sorted, so a build
    /// report reads the same twice), with the tool and VCS folders skipped. <see cref="IsTextFile"/>
    /// decides which ones the store can read: the walk itself opens nothing, because the background pass
    /// runs it every couple of seconds and the caller can cache that answer per file.
    /// </summary>
    public static IEnumerable<string> FilesUnder(string? root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!IsNoise(Path.GetFileName(entry))) pending.Push(entry);
                    continue;
                }

                yield return Path.GetFullPath(entry);
            }
        }
    }

    /// <summary>Whether a file is small enough and looks like text (no NUL byte in its head).</summary>
    public static bool IsTextFile(string path) => IsTextFile(path, out _);

    /// <summary>
    /// The same answer, with the timestamp the decision was made at: a caller that runs repeatedly can
    /// reuse it until the file moves, instead of opening every file of the solution every pass.
    /// </summary>
    public static bool IsTextFile(string path, out long mtime)
    {
        mtime = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            mtime = info.LastWriteTimeUtc.Ticks;
            if (info.Length > MaxFileBytes) return false;

            using var stream = info.OpenRead();
            var head = new byte[(int)Math.Min(SniffBytes, info.Length)];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Array.IndexOf(head, (byte)0, 0, read) < 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The language tag a path is stored with: <c>cs</c>, <c>vb</c> and <c>razor</c> are the ones a
    /// frontend reads; anything else is the extension itself (<c>md</c>, <c>json</c>), or <c>text</c>.
    /// </summary>
    public static string LanguageTag(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "cs",
        ".vb" => "vb",
        ".cshtml" or ".razor" => "razor",
        { Length: > 1 } extension => extension[1..],
        _ => "text",
    };
}
