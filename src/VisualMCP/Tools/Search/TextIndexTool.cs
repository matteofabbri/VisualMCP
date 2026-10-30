using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Search;

namespace VisualMCP.Tools.Search;

[McpServerToolType]
public static class TextIndexTool
{
    [McpServerTool(Name = "text_index"), Description(
        "Full-text index of the whole solution, stored in .visualMCP/visualmcp.db: one FTS5 row per line of every " +
        "source, document and decompiled file, with identifiers split into words (so 'required' finds " +
        "GetRequiredService) and BM25 ranking. action=search answers file:line + the matching line, the files that " +
        "match most, and optionally the lines around each hit — IN ONE CALL: prefer it to search_text + read_file " +
        "when you are looking for where something is used, named or documented and you do not need a regex. " +
        "action=build indexes what changed since the last build (a search on an empty index builds it first), " +
        "action=status says what is stored, action=files lists the indexed documents.")]
    public static Task<object> TextIndex(
        IFullTextIndexService index,
        [Description("search | build | status | files (default: search).")] string action = "search",
        [Description("search: the words to look for. Words are ANDed and prefix-matched, so 'get required' finds " +
                     "GetRequiredService; quote a phrase ('exact phrase'); '-word' excludes. Raw FTS5 syntax " +
                     "(AND/OR/NOT, *, parentheses) is used as given when the query contains it.")] string? query = null,
        [Description("search: only files whose path matches this glob, e.g. '*.cs' or '*/Tests/*'.")] string? glob = null,
        [Description("search: how many matching lines to return (default 40, max 500). At most 3 per file.")] int limit = 40,
        [Description("search: lines of context around each hit, read from the index (0-8, default 0).")] int contextLines = 0,
        [Description("build: index this folder instead of the solution root.")] string? path = null,
        [Description("build: re-index every file even when its content and timestamp did not change.")] bool force = false,
        [Description("files: only this kind — 'source', 'doc' or 'decompiled'.")] string? kind = null,
        CancellationToken cancellationToken = default)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "search" => index.SearchAsync(query ?? string.Empty, glob, limit, contextLines, cancellationToken),
            "build" => index.BuildAsync(path, force, cancellationToken),
            "status" => Task.FromResult(index.Status()),
            "files" => Task.FromResult(index.Documents(kind, limit)),
            _ => Task.FromResult<object>(new { error = "action must be search, build, status or files." }),
        };
    }
}
