using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Rename namespace": renames the last segment of a namespace and lets Roslyn's renamer
/// update every declaration, <c>using</c> directive and qualified reference in the solution. The
/// result is compiler-gated, so a rename that would leave a file unable to see a type is refused.
/// </summary>
public interface IRenameNamespaceService
{
    Task<object> RenameNamespace(
        string oldNamespace,
        string newName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class RenameNamespaceService : IRenameNamespaceService
{
    private readonly IRoslynWorkspace _workspace;

    public RenameNamespaceService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RenameNamespace(
        string oldNamespace,
        string newName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oldNamespace)) return new { error = "The namespace to rename is required." };
        if (string.IsNullOrWhiteSpace(newName)) return new { error = "The new name is required." };
        if (!IsDottedName(oldNamespace)) return new { error = $"'{oldNamespace}' is not a valid namespace name." };
        if (!SyntaxFacts.IsValidIdentifier(newName))
            return new { error = $"'{newName}' is not a simple identifier. rename_namespace renames the last segment; pass e.g. 'Core'." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        INamespaceSymbol? symbol = null;
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            symbol = Flatten(compilation.GlobalNamespace)
                .FirstOrDefault(candidate => candidate.ToDisplayString() == oldNamespace);
            if (symbol is not null) break;
        }

        if (symbol is null) return new { error = $"Namespace '{oldNamespace}' was not found in the solution." };
        if (string.Equals(symbol.Name, newName, StringComparison.Ordinal))
            return new { error = $"'{oldNamespace}' already ends with '{newName}'." };

        Solution renamed;
        try
        {
            renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, cancellationToken);
        }
        catch (Exception ex)
        {
            return new { error = $"Roslyn refused the rename: {ex.Message}" };
        }

        var changes = renamed.GetChanges(solution);
        var projects = changes.GetProjectChanges().Select(change => change.ProjectId).ToList();
        var changedDocuments = changes.GetProjectChanges()
            .SelectMany(change => change.GetChangedDocuments())
            .Select(id => renamed.GetDocument(id))
            .Where(document => document is not null)
            .Select(document => document!)
            .ToList();

        if (changedDocuments.Count == 0) return new { error = "The rename produced no changes." };

        var gate = await RefactoringCompilerGate.CompareAsync(solution, renamed, projects, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the rename");

        var previews = new List<object>();
        foreach (var document in changedDocuments)
        {
            var before = await solution.GetDocument(document.Id)!.GetTextAsync(cancellationToken);
            var after = await document.GetTextAsync(cancellationToken);
            previews.Add(new
            {
                file = document.FilePath,
                preview = RefactoringFileWriter.Diff(before.ToString(), after.ToString()),
            });
        }

        if (!dryRun)
        {
            foreach (var document in changedDocuments)
            {
                var text = (await document.GetTextAsync(cancellationToken)).ToString();

                // The document from the renamed solution already carries the new text; the write guard
                // compares the MODEL against disk, so it must be handed the pre-rename document or it
                // would report every renamed file as "changed on disk".
                var original = solution.GetDocument(document.Id);
                if (original is null)
                {
                    await RefactoringFileWriter.WriteAsync(document.FilePath!, text, cancellationToken);
                    continue;
                }

                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, original, text, cancellationToken);
            }
        }

        return new
        {
            @refactoring = "rename_namespace",
            from = oldNamespace,
            to = symbol.ContainingNamespace is { IsGlobalNamespace: false } parent
                ? parent.ToDisplayString() + "." + newName
                : newName,
            filesChanged = changedDocuments.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = previews,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static bool IsDottedName(string name) =>
        name.Split('.').All(part => part.Length > 0 && SyntaxFacts.IsValidIdentifier(part));

    private static IEnumerable<INamespaceSymbol> Flatten(INamespaceSymbol root)
    {
        yield return root;
        foreach (var child in root.GetNamespaceMembers())
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
