using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using System.ComponentModel;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

public interface IPreviewRenameService
{
    Task<object> PreviewRename(string currentName, string newName, string? containingType);
}

public sealed class PreviewRenameService : IPreviewRenameService
{
    private readonly IRoslynWorkspace _workspace;
    public PreviewRenameService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> PreviewRename(
        [Description("Current symbol name (class, method, property, field, etc.)")] string currentName,
        [Description("New name to rename to")] string newName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

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
                    oldDoc.FilePath,
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
