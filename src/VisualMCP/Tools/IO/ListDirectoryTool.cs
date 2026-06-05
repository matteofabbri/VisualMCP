using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.IO;

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
        => IoImpl.ListDirectory(path, pattern, recursive, nameContainsAny, maxEntries);
}
