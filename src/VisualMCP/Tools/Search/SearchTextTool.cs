using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using Snitch.Search;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.Search;

[McpServerToolType]
public static class SearchTextTool
{
    [McpServerTool(Name = "search_text"), Description(
        "Regex text search across files under a directory (grep-like), returning numbered lines with optional context " +
        "(a '>' marks each matching line). Works on ANY files — C/C++ headers, configs, logs, non-.NET sources — so use it " +
        "INSTEAD OF a shell 'cd … && grep'. Filter files by glob, recurse, ignore case. Read-only. " +
        "If you mean to READ the code you find, use search_code instead: same regex, but every hit comes back as the whole " +
        "declaration that contains it, so it replaces search_text followed by read_file. " +
        "(For C# symbols prefer read_symbol to get a declaration's body, or find_symbol/find_references to locate usages.)")]
    public static CallToolResult SearchText(
        ISearchTextService search,
        [Description("Regex pattern to search for.")] string pattern,
        [Description("Directory (or single file) to search in.")] string path,
        [Description("Glob filter for file names, e.g. '*.h' or '*' (default: '*').")] string glob = "*",
        [Description("Recurse into subdirectories (default: true).")] bool recursive = true,
        [Description("Case-insensitive match (default: false).")] bool ignoreCase = false,
        [Description("Maximum matches to return (default: 200).")] int maxMatches = 200,
        [Description("Lines of context before/after each match (0-10, default: 0).")] int contextLines = 0)
    {
        var result = EngineJson.As<EngineTextSearch>(
            search.Run(pattern, path, glob, recursive, ignoreCase, maxMatches, contextLines));
        if (result.Error is not null) return ToolText.Fail(result.Error);

        var builder = new StringBuilder();
        builder.Append($"pattern \"{result.Pattern}\" in {result.Path} (glob {result.Glob}, recursive {result.Recursive.ToString().ToLowerInvariant()}) — ")
               .Append(result.MatchCount).Append(" match(es) in ")
               .Append(result.FilesWithMatches).Append(" of ").Append(result.FilesScanned).Append(" file(s)");
        if (result.Truncated) builder.Append(" — TRUNCATED at maxMatches");
        builder.Append('\n');

        string? currentFile = null;
        foreach (var match in result.Matches ?? [])
        {
            if (match.File != currentFile)
            {
                builder.Append('\n').Append(match.File).Append('\n');
                currentFile = match.File;
            }

            if (match.Context is { Count: > 0 })
            {
                foreach (var line in match.Context)
                    builder.Append(line.Line == match.Line ? '>' : ' ')
                           .Append(line.Line.ToString().PadLeft(5)).Append(": ")
                           .Append(line.Text).Append('\n');
            }
            else
            {
                builder.Append('>')
                       .Append(match.Line.ToString().PadLeft(5)).Append(": ")
                       .Append(match.Text).Append('\n');
            }
        }

        return ToolText.Ok(builder.ToString().TrimEnd('\n'));
    }
}
