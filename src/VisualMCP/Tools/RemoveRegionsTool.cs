using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class RemoveRegionsTool
{
    [McpServerTool, Description("Remove all #region and #endregion directives from source files, leaving the code inside intact. Equivalent to CodeMaid 'Remove Regions'. Requires LoadSolution first.")]
    public static async Task<object> RemoveRegions(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Dry run — report which files would change without writing (default: false)")] bool dryRun = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var results = new List<object>();
        int totalRegions = 0;

        foreach (var project in projects)
        {
            var docs = project.Documents
                .Where(d => d.SourceCodeKind == SourceCodeKind.Regular &&
                            (filePath is null || string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var document in docs)
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null || document.FilePath is null) continue;

                // Collect all #region and #endregion directives
                var directives = root.DescendantTrivia()
                    .Where(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia) ||
                                t.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
                    .ToList();

                if (directives.Count == 0) continue;

                totalRegions += directives.Count(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia));

                // Remove directive lines from the source text
                // Work on raw text: remove the entire line for each directive
                var text = (await document.GetTextAsync()).ToString();
                var lines = text.Split('\n').ToList();

                var linesToRemove = new HashSet<int>();
                foreach (var trivia in directives)
                {
                    var lineSpan = trivia.GetLocation().GetLineSpan();
                    linesToRemove.Add(lineSpan.StartLinePosition.Line);
                }

                var newLines = lines
                    .Select((line, idx) => (line, idx))
                    .Where(x => !linesToRemove.Contains(x.idx))
                    .Select(x => x.line)
                    .ToList();

                var newText = string.Join('\n', newLines);

                results.Add(new
                {
                    FilePath = document.FilePath,
                    RegionsRemoved = directives.Count(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia)),
                });

                if (!dryRun)
                    await File.WriteAllTextAsync(document.FilePath, newText, System.Text.Encoding.UTF8);
            }
        }

        return new
        {
            dryRun,
            projectFilter = projectName ?? "all",
            fileFilter = filePath ?? "all",
            filesModified = results.Count,
            totalRegionsRemoved = totalRegions,
            files = results,
        };
    }
}
