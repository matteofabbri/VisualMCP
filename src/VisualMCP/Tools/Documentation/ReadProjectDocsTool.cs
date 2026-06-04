using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Documentation;

[McpServerToolType]
public static class ReadProjectDocsTool
{
    [McpServerTool(Name = "read_project_docs"), Description(
        "Read the solution's Markdown and documentation files (README, *.md/.markdown/.mdx, docs, " +
        "CHANGELOG, ARCHITECTURE, etc.), which are indexed and cached when the solution opens. " +
        "Call this FIRST when you start working on a solution to understand how the project is organised " +
        "before reading code. Returns a file index and (by default) their content, budgeted so it does not " +
        "flood the context. The solution auto-loads on first use.")]
    public static async Task<object> ReadProjectDocs(
        [Description("Include file content, not just the index (default: true).")] bool includeContent = true,
        [Description("Optional: only include files whose relative path contains this substring (case-insensitive), e.g. 'README' or 'docs'.")] string? nameFilter = null,
        [Description("Maximum total characters of content to return across all files (default: 60000).")] int maxChars = 60000)
    {
        var svc = RoslynWorkspaceService.Instance;
        await svc.EnsureSolutionLoadedAsync();

        var root = svc.LoadedSolutionPath is { } sln
            ? Path.GetDirectoryName(sln)!
            : Directory.GetCurrentDirectory();

        var (resolvedRoot, docs) = ProjectDocsService.Get(root);

        var filtered = docs
            .Where(d => nameFilter is null || d.RelativePath.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var budget = Math.Clamp(maxChars, 1000, 400_000);
        var truncatedForBudget = false;

        var files = filtered.Select(d =>
        {
            string? content = null;
            if (includeContent && d.Content is not null)
            {
                if (budget <= 0)
                {
                    truncatedForBudget = true;
                }
                else if (d.Content.Length > budget)
                {
                    content = d.Content[..budget] + "\n…(truncated — content budget reached)";
                    budget = 0;
                    truncatedForBudget = true;
                }
                else
                {
                    content = d.Content;
                    budget -= d.Content.Length;
                }
            }

            return new
            {
                d.RelativePath,
                d.SizeBytes,
                contentOmitted = d.ContentOmitted,
                content,
            };
        }).ToList();

        return new
        {
            root = resolvedRoot,
            fileCount = filtered.Count,
            totalDocFiles = docs.Count,
            contentBudgetExhausted = truncatedForBudget,
            files,
        };
    }
}
