using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class PreviewRenameTool
{
    [McpServerTool, Description("Preview what a symbol rename would change across the solution without applying it — equivalent to Visual Studio's Rename refactoring preview. Requires LoadSolution first.")]
    public static async Task<object> PreviewRename(
        [Description("Current symbol name (class, method, property, field, etc.)")] string currentName,
        [Description("New name to rename to")] string newName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        if (string.IsNullOrWhiteSpace(newName))
            return new { error = "newName must not be empty." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(currentName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.All);

        var symbols = candidates
            .Where(s => containingType is null ||
                        (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0)
            return new { error = $"Symbol '{currentName}' not found." };
        if (symbols.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {symbols.Count} symbols named '{currentName}'. Specify containingType to disambiguate.",
                candidates = symbols.Select(s => s.ToDisplayString()).ToList(),
            };

        var symbol = symbols[0];

        var options = new SymbolRenameOptions(
            RenameOverloads: false,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false);

        Solution renamedSolution;
        try
        {
            renamedSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName);
        }
        catch (Exception ex)
        {
            return new { error = $"Rename failed: {ex.Message}" };
        }

        // Diff: find documents whose text changed
        var changes = new List<object>();
        foreach (var projectId in solution.ProjectIds)
        {
            var oldProject = solution.GetProject(projectId);
            var newProject = renamedSolution.GetProject(projectId);
            if (oldProject is null || newProject is null) continue;

            foreach (var docId in oldProject.DocumentIds)
            {
                var oldDoc = oldProject.GetDocument(docId);
                var newDoc = newProject.GetDocument(docId);
                if (oldDoc is null || newDoc is null) continue;

                var oldText = await oldDoc.GetTextAsync();
                var newText = await newDoc.GetTextAsync();
                if (oldText.ContentEquals(newText)) continue;

                // Produce unified diff (line-level)
                var oldLines = oldText.ToString().Split('\n');
                var newLines = newText.ToString().Split('\n');
                var diffLines = ProduceDiff(oldLines, newLines);

                changes.Add(new
                {
                    FilePath  = oldDoc.FilePath,
                    DiffLines = diffLines,
                });
            }
        }

        return new
        {
            symbol     = symbol.ToDisplayString(),
            kind       = symbol.Kind.ToString(),
            currentName,
            newName,
            filesAffected = changes.Count,
            changes,
        };
    }

    private static List<string> ProduceDiff(string[] oldLines, string[] newLines)
    {
        // Simple line-level diff: emit context lines around changes
        var result = new List<string>();
        int max = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < max; i++)
        {
            var o = i < oldLines.Length ? oldLines[i] : null;
            var n = i < newLines.Length ? newLines[i] : null;
            if (o != n)
            {
                if (o is not null) result.Add($"-{i + 1}: {o}");
                if (n is not null) result.Add($"+{i + 1}: {n}");
            }
        }
        return result;
    }
}
