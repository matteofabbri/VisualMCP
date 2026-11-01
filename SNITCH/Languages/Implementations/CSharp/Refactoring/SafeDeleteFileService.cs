using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Delete a file or a folder only when nothing outside it uses what it declares (Rider's "Safe
/// delete" extended to files and folders). It refuses, listing the uses, when a symbol leaks out;
/// a folder that holds anything but C# files is refused as well, because a .cs-only rule cannot
/// judge the rest. Preview by default: deletion is destructive.
/// </summary>
public interface ISafeDeleteFileService
{
    Task<object> DeleteFile(string filePath, bool dryRun, CancellationToken cancellationToken);
    Task<object> DeleteFolder(string folderPath, bool dryRun, CancellationToken cancellationToken);
}

public sealed class SafeDeleteFileService : ISafeDeleteFileService
{
    private readonly IRoslynWorkspace _workspace;
    public SafeDeleteFileService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> DeleteFile(string filePath, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var references = await ExternalReferencesAsync(solution, [document], path, cancellationToken);
        if (references.Count > 0)
            return new
            {
                error = $"{Path.GetFileName(path)} is used outside itself: {string.Join(", ", references.Take(10))}.",
                uses = references.Take(20).ToList(),
                hint = "Remove or move those uses first; nothing was deleted.",
            };

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document,
                (await document.GetTextAsync(cancellationToken)).ToString(), cancellationToken);
            File.Delete(path);
            _workspace.TryRemoveDocument(document.Id);
        }

        return new
        {
            deleted = !dryRun,
            dryRun,
            file = path,
            declared = await DeclaredNamesAsync(document, cancellationToken),
            note = dryRun
                ? "Preview only: pass dryRun=false to delete the file."
                : "File deleted and removed from the Roslyn model.",
        };
    }

    public async Task<object> DeleteFolder(string folderPath, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return new { error = "A folder path is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        string folder;
        try { folder = Path.GetFullPath(folderPath); }
        catch (Exception ex) { return new { error = $"Invalid path '{folderPath}': {ex.Message}" }; }

        if (!Directory.Exists(folder)) return new { error = $"Folder not found: {folder}" };

        var foreign = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (foreign.Count > 0)
            return new
            {
                error = "The folder holds files that are not C#; a reference check cannot judge them.",
                files = foreign.Take(20).ToList(),
                hint = "Delete them explicitly, or move them out of the folder first.",
            };

        var documents = solution.Projects.SelectMany(project => project.Documents)
            .Where(candidate => candidate.FilePath is not null &&
                                Path.GetFullPath(candidate.FilePath)
                                    .StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var references = await ExternalReferencesAsync(solution, documents, folder, cancellationToken);
        if (references.Count > 0)
            return new
            {
                error = $"{references.Count} use(s) outside the folder keep its declarations alive.",
                uses = references.Take(20).ToList(),
                hint = "Nothing was deleted.",
            };

        if (!dryRun)
        {
            Directory.Delete(folder, recursive: true);
            foreach (var document in documents) _workspace.TryRemoveDocument(document.Id);
        }

        return new
        {
            deleted = !dryRun,
            dryRun,
            folder,
            files = documents.Count,
            note = dryRun
                ? "Preview only: pass dryRun=false to delete the folder."
                : "Folder deleted and its documents removed from the Roslyn model.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static async Task<List<string>> ExternalReferencesAsync(Solution solution, List<Document> documents,
        string boundary, CancellationToken cancellationToken)
    {
        var uses = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            var model = await document.GetSemanticModelAsync(cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (model is null || root is null) continue;

            var declarations = root.DescendantNodes()
                .OfType<MemberDeclarationSyntax>()
                .Where(member => member.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .Where(member => member is not BaseNamespaceDeclarationSyntax)
                .ToList();

            foreach (var declaration in declarations)
            {
                var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
                if (symbol is null) continue;

                foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
                {
                    foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
                    {
                        var file = location.Document.FilePath;
                        if (file is null) continue;
                        if (file.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(file, boundary, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                        if (seen.Add($"{file}:{line}"))
                            uses.Add($"{Path.GetFileName(file)}:{line} ({symbol.Name})");
                    }
                }
            }
        }

        return uses;
    }

    private static async Task<List<string>> DeclaredNamesAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (model is null || root is null) return [];

        return root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(member => member.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(member => member is not BaseNamespaceDeclarationSyntax)
            .Select(member => model.GetDeclaredSymbol(member, cancellationToken)?.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
    }
}
