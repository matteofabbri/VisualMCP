using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ListDirectoryTool
{
    [McpServerTool(Name = "list_directory"), Description(
        "List a directory's contents (files and immediate subdirectories) with sizes and timestamps — a " +
        "read-only inventory of a folder (build/publish output, a repo root, a library install). Use this " +
        "INSTEAD OF a shell 'ls' / 'Get-ChildItem' / 'dir' to see what's present, how big files are, and " +
        "how many there are. Filter files by glob pattern and/or name substrings, and optionally recurse. " +
        "For a single file, returns that file's info.")]
    public static object ListDirectory(
        [Description("Directory (or file) path to inspect.")] string path,
        [Description("Glob filter for file names, e.g. '*.dll' or '*' for all (default: '*').")] string pattern = "*",
        [Description("Recurse into subdirectories (default: false).")] bool recursive = false,
        [Description("Optional: only include files whose name contains ANY of these substrings (case-insensitive).")] string[]? nameContainsAny = null,
        [Description("Maximum number of file entries to return (default: 500).")] int maxEntries = 500)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new { error = "No path provided." };

        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return new
            {
                path,
                exists = true,
                type = "file",
                sizeBytes = fi.Length,
                sizeMB = Mb(fi.Length),
                lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }

        if (!Directory.Exists(path))
            return new { path, exists = false };

        if (maxEntries < 1) maxEntries = 1;
        if (maxEntries > 5000) maxEntries = 5000;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible    = true,
            MatchType             = MatchType.Simple,
        };

        long totalSize = 0;
        var matched = 0;
        var entries = new List<(string name, string rel, long size, DateTime modified)>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern, options))
            {
                var name = Path.GetFileName(file);
                if (nameContainsAny is { Length: > 0 } &&
                    !nameContainsAny.Any(s => !string.IsNullOrEmpty(s) && name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    continue;

                long size;
                DateTime modified;
                try { var fi = new FileInfo(file); size = fi.Length; modified = fi.LastWriteTime; }
                catch { size = 0; modified = default; }

                matched++;
                totalSize += size;
                if (entries.Count < maxEntries)
                    entries.Add((name, Path.GetRelativePath(path, file), size, modified));
            }
        }
        catch (ArgumentException ex)
        {
            return new { error = $"Invalid pattern '{pattern}': {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new { error = $"Failed to list '{path}': {ex.Message}" };
        }

        var files = entries
            .OrderBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new
            {
                e.name,
                relativePath = e.rel,
                sizeBytes = e.size,
                sizeMB = Mb(e.size),
                lastModified = e.modified == default ? null : e.modified.ToString("yyyy-MM-dd HH:mm:ss"),
            })
            .ToList();

        List<string>? subdirectories = null;
        if (!recursive)
        {
            try
            {
                subdirectories = Directory.EnumerateDirectories(path)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()!;
            }
            catch { /* best-effort */ }
        }

        return new
        {
            path,
            exists = true,
            type = "directory",
            pattern,
            recursive,
            matchedFileCount = matched,
            matchedTotalSizeBytes = totalSize,
            matchedTotalSizeMB = Mb(totalSize),
            returnedCount = files.Count,
            truncated = matched > files.Count,
            subdirectories,
            files,
        };
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1024d / 1024d, 2);
}
