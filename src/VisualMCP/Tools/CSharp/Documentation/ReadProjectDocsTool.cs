using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Documentation;

namespace VisualMCP.Tools.CSharp.Documentation;

[McpServerToolType]
public static class ReadProjectDocsTool
{
    [McpServerTool(Name = "read_project_docs"), Description(
        "Read the solution's Markdown and documentation files (README, *.md/.markdown/.mdx, docs, " +
        "CHANGELOG, ARCHITECTURE, etc.), which are indexed and cached when the solution opens. " +
        "Call this FIRST when you start working on a solution to understand how the project is organised " +
        "before reading code. Returns a file index and (by default) their content, budgeted so it does not " +
        "flood the context. The solution auto-loads on first use.")]
    public static Task<object> ReadProjectDocs(
        [Description("Include file content, not just the index (default: true).")] bool includeContent = true,
        [Description("Optional: only include files whose relative path contains this substring (case-insensitive), e.g. 'README' or 'docs'.")] string? nameFilter = null,
        [Description("Maximum total characters of content to return across all files (default: 60000).")] int maxChars = 60000)
        => DocumentationImpl.ReadProjectDocsAsync(includeContent, nameFilter, maxChars);
}
