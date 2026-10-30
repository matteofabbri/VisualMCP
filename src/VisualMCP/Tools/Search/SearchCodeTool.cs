using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Search;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.Search;

[McpServerToolType]
public static class SearchCodeTool
{
    [McpServerTool(Name = "search_code"), Description(
        "Regex search that returns THE CODE, not just the matching lines: every hit is resolved to the declaration that " +
        "contains it (method, property, field, type — with its XML docs) and printed in full, so ONE call replaces the " +
        "search_text → read_file → read_file sequence. Files that are not part of the loaded solution (logs, configs, " +
        "non-.NET sources) come back as a window of lines around each match. A file that matches many times is printed " +
        "once per declaration with all its match lines. The output is capped (maxChars, maxMatches) and anything left out " +
        "is named at the end. Prefer this to search_text whenever you intend to read the code you find; use search_text " +
        "itself only when you want the raw matching lines of a non-code file.")]
    public static async Task<CallToolResult> SearchCode(
        ICodeSearchService search,
        [Description("Regex pattern to search for.")] string pattern,
        [Description("Directory (or single file) to search in.")] string path,
        [Description("Glob filter for file names, e.g. '*.cs' (default) or '*'. Use '*' for logs and configs.")] string glob = "*.cs",
        [Description("Case-insensitive match (default: false).")] bool ignoreCase = false,
        [Description("Maximum matches to look for (default 100, max 2000).")] int maxMatches = 100,
        [Description("Total output budget in characters (2000-180000, default 60000).")] int maxChars = 60_000,
        [Description("Lines of context for files outside the solution (0-20, default 8).")] int contextLines = 8,
        CancellationToken cancellationToken = default)
    {
        var result = await search.RunAsync(
            pattern, path, glob, ignoreCase, maxMatches, maxChars, contextLines, cancellationToken);

        if (result.Error is not null) return ToolText.Fail(result.Error);

        var builder = new StringBuilder();
        builder.Append($"pattern \"{result.Pattern}\" in {result.Root} (glob {result.Glob}) — ")
               .Append(result.MatchCount).Append(" match(es) in ")
               .Append(result.FilesWithMatches).Append(" of ").Append(result.FilesScanned).Append(" file(s) — ")
               .Append(result.DeclarationCount).Append(" declaration(s)");

        if (result.Truncated) builder.Append(" — TRUNCATED at maxMatches");
        builder.Append('\n');

        string? currentFile = null;
        foreach (var hit in result.Hits)
        {
            if (!string.Equals(hit.File, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('\n').Append("=== ").Append(hit.File).Append('\n');
                currentFile = hit.File;
            }

            builder.Append("--- ");
            if (hit.Kind is null)
                builder.Append("lines ").Append(hit.Line).Append('-').Append(hit.EndLine);
            else
                builder.Append(hit.Kind).Append(' ').Append(hit.Container is null ? string.Empty : hit.Container + ".");

            if (hit.Signature is not null) builder.Append(hit.Signature);
            builder.Append(" (lines ").Append(hit.Line).Append('-').Append(hit.EndLine).Append(")");
            builder.Append(" · match line(s) ").Append(string.Join(", ", hit.Matches));
            builder.Append('\n');

            builder.Append(hit.Source).Append("\n\n");
        }

        if (result.Hits.Count == 0) builder.Append("No match.\n");

        if (result.LeftOut.Count > 0)
        {
            builder.Append("—\n").Append(result.LeftOut.Count)
                   .Append(" hit(s) left out (budget or per-file cap):\n  - ")
                   .Append(string.Join("\n  - ", result.LeftOut.Take(20)));
            if (result.LeftOut.Count > 20) builder.Append("\n  … and ").Append(result.LeftOut.Count - 20).Append(" more");
            builder.Append('\n');
        }

        return ToolText.Ok(builder.ToString().TrimEnd('\n'));
    }
}
