using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Search;

namespace VisualMCP.Tools.Search;

[McpServerToolType]
public static class SearchTextTool
{
    [McpServerTool(Name = "search_text"), Description(
        "Regex text search across files under a directory (grep-like), returning file:line:text matches with " +
        "optional context. Works on ANY files — C/C++ headers, configs, logs, non-.NET sources — so use it " +
        "INSTEAD OF a shell 'cd … && grep'. Filter files by glob, recurse, ignore case. Read-only. " +
        "(For C# symbols specifically prefer find_symbol/find_references — they're semantic.)")]
    public static object SearchText(
        [Description("Regex pattern to search for.")] string pattern,
        [Description("Directory (or single file) to search in.")] string path,
        [Description("Glob filter for file names, e.g. '*.h' or '*' (default: '*').")] string glob = "*",
        [Description("Recurse into subdirectories (default: true).")] bool recursive = true,
        [Description("Case-insensitive match (default: false).")] bool ignoreCase = false,
        [Description("Maximum matches to return (default: 200).")] int maxMatches = 200,
        [Description("Lines of context before/after each match (0-10, default: 0).")] int contextLines = 0)
        => SearchTextImpl.Run(pattern, path, glob, recursive, ignoreCase, maxMatches, contextLines);
}
