namespace Snitch.IO;

/// <summary>
/// The engine's filesystem and solution-level operations: opening a solution, scanning for
/// solution files, reading a file, reporting the workspace state, describing a project,
/// inventorying a directory and moving a path.
/// </summary>
/// <remarks>
/// Every method returns one of the typed results declared in <c>IoResults.cs</c>, so a caller
/// reads the payload through the compiler instead of guessing at the members of an anonymous
/// object. A failure travels in <see cref="IoResult.Error"/> rather than an exception.
/// </remarks>
public interface IIoService
{
    /// <summary>Opens the solution at <paramref name="path"/> and makes it the active one.</summary>
    Task<LoadSolutionResult> LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Scans <paramref name="rootPath"/> for .sln/.slnx files, down to <paramref name="maxDepth"/>.</summary>
    ListSolutionsResult ListSolutions(string rootPath, int maxDepth);

    /// <summary>Reads a file — optionally a 1-based line range — and returns its numbered lines.</summary>
    ReadFileResult ReadFile(string path, int? fromLine, int? toLine);

    /// <summary>Reports whether a C# solution is loaded, and how many projects it holds.</summary>
    CheckSolutionResult CheckSolution();

    /// <summary>Describes one project of the loaded solution: files, references and packages.</summary>
    Task<ProjectInfoResult> GetProjectInfoAsync(string nameOrPath, CancellationToken cancellationToken = default);

    /// <summary>Inventories a directory, or describes a single file.</summary>
    ListDirectoryResult ListDirectory(string path, string pattern, bool recursive, string[]? nameContainsAny, int maxEntries);

    /// <summary>Moves or renames a file or directory, creating the destination's parent folders.</summary>
    MovePathResult MovePath(string source, string destination, bool overwrite);
}
