using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Documentation;
using Snitch.Planning;
using VisualMCP.Startup;

namespace VisualMCP.Tools.CSharp.Documentation;

[McpServerToolType]
public static class ReadProjectDocsTool
{
    /// <summary>Matches the server's own serializer (web defaults), so the merged node keeps the response's shape.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "read_project_docs"), Description(
        "Read the solution's Markdown and documentation files (README, *.md/.markdown/.mdx, docs, " +
        "CHANGELOG, ARCHITECTURE, etc.), which are indexed and cached when the solution opens. " +
        "Call this FIRST when you start working on a solution to understand how the project is organised " +
        "before reading code. Returns a file index and (by default) their content, budgeted so it does not " +
        "flood the context. The answer carries a 'board' block — the project's plan as counts, the card to " +
        "pick up, how many durable notes the board holds and which ones are pinned — because what is missing and " +
        "what comes next live on the board, not in these documents. The first call of a session also reports " +
        "memoryImport when notes kept by another client were imported onto this board — this server has no " +
        "memory_* tool: the memory is board_remember/board_recall.")]
    public static async Task<object> ReadProjectDocs(
        IDocumentationService docs,
        IMemoryImporter memoryImport,
        IBoardService board,
        [Description("Include file content, not just the index (default: true).")] bool includeContent = true,
        [Description("Optional: only include files whose relative path contains this substring (case-insensitive), e.g. 'README' or 'docs'.")] string? nameFilter = null,
        [Description("Maximum total characters of content to return across all files (default: 60000).")] int maxChars = 60000)
    {
        var result = await docs.ReadProjectDocsAsync(includeContent, nameFilter, maxChars).ConfigureAwait(false);

        // Two notices ride along: the memory that moved in from another client (once per session) and the
        // board's state. A notice must never fail the docs read, so a payload this tool cannot re-shape —
        // or a board that has no database yet — comes back untouched.
        var notice = await memoryImport.TakeNoticeAsync().ConfigureAwait(false);
        var boardState = await board.SummaryAsync().ConfigureAwait(false);

        try
        {
            if (JsonSerializer.SerializeToNode(result, Json) is not JsonObject document) return result;

            var attached = Attach(document, "memoryImport", notice);
            attached |= Attach(document, "board", boardState);

            // Nothing to say: the documentation comes back exactly as the engine returned it.
            return attached ? document : result;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return result; /* best-effort: the documentation is what the caller asked for */
        }

        static bool Attach(JsonObject document, string property, object? value)
        {
            if (value is null) return false;

            var node = JsonSerializer.SerializeToNode(value, Json);

            // An unavailable service answers { error }: attach nothing rather than a block that only says so.
            if (node is JsonObject failure && failure.ContainsKey("error")) return false;
            document[property] = node;
            return true;
        }
    }
}
