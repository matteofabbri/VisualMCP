using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class ApplyRenameTool
{
    [McpServerTool, Description("Rename a symbol across the entire solution and write the changes to disk. Use preview_rename first to verify the impact. Requires LoadSolution first.")]
    public static async Task<object> ApplyRename(
        [Description("Current symbol name (class, method, property, field, etc.)")] string currentName,
        [Description("New name to rename to")] string newName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null,
        [Description("Also rename overloads with the same name (default: false)")] bool renameOverloads = false,
        [Description("Also rename occurrences inside string literals (default: false)")] bool renameInStrings = false,
        [Description("Also rename occurrences inside comments (default: false)")] bool renameInComments = false)
    {
        var svc = RoslynWorkspaceService.Instance;
        var solution = svc.CurrentSolution;
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
            RenameOverloads: renameOverloads,
            RenameInStrings: renameInStrings,
            RenameInComments: renameInComments,
            RenameFile: false);

        Solution renamedSolution;
        try
        {
            renamedSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName);
        }
        catch (Exception ex)
        {
            return new { error = $"Rename computation failed: {ex.Message}" };
        }

        // Write changed documents to disk
        var written = new List<string>();
        var errors = new List<string>();

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

                var path = oldDoc.FilePath;
                if (path is null) continue;

                try
                {
                    await File.WriteAllTextAsync(path, newText.ToString(), System.Text.Encoding.UTF8);
                    written.Add(path);
                }
                catch (Exception ex)
                {
                    errors.Add($"{path}: {ex.Message}");
                }
            }
        }

        return new
        {
            symbol = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            currentName,
            newName,
            filesModified = written.Count,
            filesWritten = written,
            writeErrors = errors,
        };
    }
}
