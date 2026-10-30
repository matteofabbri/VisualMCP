using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ReadTreeTool
{
    private static readonly string[] BuildFolders = { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };

    [McpServerTool(Name = "read_tree"), Description(
        "Show a directory TREE down to a chosen depth in ONE call: subdirectories, matching files and their sizes. " +
        "Use this INSTEAD OF calling list_directory once per folder to walk down a hierarchy \u2014 the whole shape " +
        "comes back at once, which is what you want at the start of an exploration. " +
        "Build/IDE folders (bin, obj, .git, .vs, .idea, node_modules) are skipped unless includeBuildFolders=true. " +
        "For one folder's exact inventory use list_directory; for a single file's text use read_file.")]
    public static CallToolResult ReadTree(
        [Description("Root directory to describe.")] string path,
        [Description("Levels of contents to expand (1-8, default 3). Deeper folders are named but not expanded.")] int depth = 3,
        [Description("Glob for file names to list, e.g. '*.cs' (default '*' = every file).")] string pattern = "*",
        [Description("Include bin, obj, .git, .vs, .idea, node_modules (default false).")] bool includeBuildFolders = false,
        [Description("Maximum entries rendered in total (50-2000, default 600).")] int maxEntries = 600)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolText.Fail("read_tree needs a directory path.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return ToolText.Fail($"Invalid path '{path}': {ex.Message}");
        }

        if (File.Exists(root))
        {
            var info = new FileInfo(root);
            return ToolText.Fail(
                $"{root} is a file ({FormatSize(info.Length)}, modified {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}). " +
                "read_tree walks directories: use read_file for the text or list_directory for its metadata.");
        }

        if (!Directory.Exists(root))
        {
            return ToolText.Fail($"Directory not found: {root}");
        }

        depth = Math.Clamp(depth, 1, 8);
        maxEntries = Math.Clamp(maxEntries, 50, 2_000);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "*";
        }

        var output = new StringBuilder();
        int rendered = 0;
        int files = 0;
        int dirs = 0;
        int pruned = 0;
        bool truncated = false;
        const int maxFilesPerDirectory = 30;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        void Walk(string directory, int level, string indent)
        {
            if (truncated)
            {
                return;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory, "*", options);
            }
            catch (Exception ex)
            {
                output.Append('\n').Append(indent).Append("(cannot read: ").Append(ex.Message).Append(')');
                return;
            }

            var childDirectories = new List<string>();
            var matchingFiles = new List<(string Name, long Size)>();
            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!includeBuildFolders && BuildFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pruned++;
                        continue;
                    }

                    childDirectories.Add(entry);
                }
                else if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                {
                    long size = 0;
                    try
                    {
                        size = new FileInfo(entry).Length;
                    }
                    catch
                    {
                        // A file that vanished between enumeration and stat keeps size 0.
                    }

                    matchingFiles.Add((name, size));
                }
            }

            childDirectories.Sort(StringComparer.OrdinalIgnoreCase);
            matchingFiles.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            files += matchingFiles.Count;
            dirs += childDirectories.Count;

            // Files first, at the folder's own indent, then the subfolders: reading
            // order matches the indentation instead of files trailing a whole subtree.
            foreach ((string name, long size) in matchingFiles.Take(maxFilesPerDirectory))
            {
                rendered++;
                output.Append('\n').Append(indent).Append(name).Append("   (").Append(FormatSize(size)).Append(')');
                if (rendered >= maxEntries)
                {
                    truncated = true;
                    return;
                }
            }

            if (matchingFiles.Count > maxFilesPerDirectory)
            {
                output.Append('\n').Append(indent).Append("\u2026 +")
                      .Append(matchingFiles.Count - maxFilesPerDirectory)
                      .Append(" more file(s) matching '").Append(pattern)
                      .Append("' in this folder (list_directory)");
            }

            foreach (string child in childDirectories)
            {
                string name = Path.GetFileName(child);
                rendered++;
                output.Append('\n').Append(indent).Append(name).Append('/');
                if (level < depth)
                {
                    Walk(child, level + 1, indent + "  ");
                }
                else
                {
                    int count = 0;
                    try
                    {
                        count = Directory.GetFileSystemEntries(child, "*", options).Length;
                    }
                    catch
                    {
                        // Count stays 0: the folder is unreadable at this depth.
                    }

                    output.Append("   (").Append(count).Append(count == 1 ? " entry" : " entries")
                          .Append(", not expanded \u2014 raise depth)");
                }

                if (rendered >= maxEntries)
                {
                    truncated = true;
                    return;
                }
            }
        }

        string rootName = Path.GetFileName(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        output.Append(string.IsNullOrEmpty(rootName) ? root : rootName).Append('/');
        Walk(root, 1, "  ");

        var footer = new StringBuilder();
        footer.Append($"\n\n\u2014\n{root} \u2014 depth {depth}, pattern '{pattern}': {files} file(s) and {dirs} folder(s) "
                      + $"counted, {rendered} entries rendered.");
        if (truncated)
        {
            footer.Append($" Stopped at the {maxEntries}-entry cap: narrow with pattern or raise maxEntries.");
        }

        if (pruned > 0)
        {
            footer.Append($" {pruned} build/IDE folder(s) skipped (includeBuildFolders=true to list them).");
        }

        footer.Append(" Paths above are relative to the root.");
        return ToolText.Ok(output.ToString().TrimStart('\n') + footer);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };
}
