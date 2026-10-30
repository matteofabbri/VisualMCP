using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Documentation;
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
        "flood the context. The solution auto-loads on first use. The first call of a session also reports " +
        "memoryImport when project memory kept by another client was carried into this client's store — " +
        "this server has no memory_* tools, so never look for them.")]
    public static async Task<object> ReadProjectDocs(
        IDocumentationService docs,
        IMemoryImporter memoryImport,
        [Description("Include file content, not just the index (default: true).")] bool includeContent = true,
        [Description("Optional: only include files whose relative path contains this substring (case-insensitive), e.g. 'README' or 'docs'.")] string? nameFilter = null,
        [Description("Maximum total characters of content to return across all files (default: 60000).")] int maxChars = 60000)
    {
        var result = await docs.ReadProjectDocsAsync(includeContent, nameFilter, maxChars).ConfigureAwait(false);

        // Say it once per session, and only when something actually moved: a notice must never fail the
        // docs read, so a payload this tool cannot re-shape comes back untouched.
        var notice = await memoryImport.TakeNoticeAsync().ConfigureAwait(false);
        if (notice is null) return result;

        try
        {
            if (JsonSerializer.SerializeToNode(result, Json) is not JsonObject document) return result;
            document["memoryImport"] = JsonSerializer.SerializeToNode(notice, Json);
            return document;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return result; /* best-effort: the documentation is what the caller asked for */
        }
    }
}
