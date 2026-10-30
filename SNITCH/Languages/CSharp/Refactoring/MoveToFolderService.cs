using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move to folder": puts a file into the folder that matches its namespace (relative to the
/// project directory) or into an explicit folder, and updates the model so the next call sees the new
/// path. The content is unchanged, so there is nothing for the compiler to re-check.
/// </summary>
public interface IMoveToFolderService
{
    Task<object> MoveToFolder(
        string filePath,
        string? folder = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MoveToFolderService : IMoveToFolderService
{
    private readonly IRoslynWorkspace _workspace;

    public MoveToFolderService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveToFolder(
        string filePath,
        string? folder = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };
        if (document.FilePath is null) return new { error = "The document has no path on disk." };

        var project = document.Project;
        var projectDirectory = Path.GetDirectoryName(project.FilePath) ?? Path.GetDirectoryName(document.FilePath);
        if (projectDirectory is null) return new { error = "The project has no directory on disk." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var namespaceName = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

        string targetDirectory;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            targetDirectory = Path.IsPathRooted(folder) ? folder : Path.Combine(projectDirectory, folder);
        }
        else
        {
            if (namespaceName is null)
                return new { error = $"{Path.GetFileName(path)} declares no namespace, so there is no folder to derive; pass folder explicitly." };

            // The root namespace is the project/assembly name in an SDK-style project, so it maps to the
            // project folder itself; everything after it becomes a subfolder.
            var relative = namespaceName;
            var assemblyName = project.AssemblyName ?? project.Name;
            if (relative.Equals(assemblyName, StringComparison.Ordinal))
                relative = string.Empty;
            else if (relative.StartsWith(assemblyName + ".", StringComparison.Ordinal))
                relative = relative[(assemblyName.Length + 1)..];

            targetDirectory = relative.Length == 0
                ? projectDirectory
                : Path.Combine(projectDirectory, relative.Replace('.', Path.DirectorySeparatorChar));
        }

        targetDirectory = Path.GetFullPath(targetDirectory);
        var currentDirectory = Path.GetDirectoryName(document.FilePath)!;
        if (string.Equals(targetDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase))
            return new { error = $"{Path.GetFileName(path)} is already in {targetDirectory}." };

        var destination = Path.Combine(targetDirectory, Path.GetFileName(document.FilePath));
        if (File.Exists(destination)) return new { error = $"{destination} already exists." };

        // The project file is not always on disk (an ad-hoc solution the workspace built in memory),
        // so the explicit-<Compile> check only runs when there is something to read.
        string? projectFile = null;
        if (project.FilePath is not null && File.Exists(project.FilePath))
            projectFile = await File.ReadAllTextAsync(project.FilePath, cancellationToken);

        if (projectFile is not null &&
            projectFile.Contains(Path.GetFileName(document.FilePath), StringComparison.OrdinalIgnoreCase) &&
            projectFile.Contains("<Compile Include", StringComparison.OrdinalIgnoreCase))
            return new { error = "The project lists this file explicitly (<Compile Include>), so moving it on disk would break the build; update the project file by hand." };

        if (dryRun)
            return new
            {
                @refactoring = "move_to_folder",
                from = document.FilePath,
                to = destination,
                @namespace = namespaceName,
                dryRun = true,
                applied = false,
                note = "Preview only: pass dryRun=false to move the file.",
            };

        Directory.CreateDirectory(targetDirectory);
        File.Move(document.FilePath, destination);

        var text = await document.GetTextAsync(cancellationToken);
        _workspace.TryRemoveDocument(document.Id);
        _workspace.TryAddDocument(project.Id, Path.GetFileName(destination), destination, text);

        return new
        {
            @refactoring = "move_to_folder",
            from = document.FilePath,
            to = destination,
            @namespace = namespaceName,
            dryRun = false,
            applied = true,
            note = "File moved and the model updated.",
        };
    }
}
