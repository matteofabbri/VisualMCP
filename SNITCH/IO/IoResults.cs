using System.Text.Json.Serialization;
using Snitch.Languages.CSharp.Parsing.Model;

namespace Snitch.IO;

/// <summary>
/// Base of every result <see cref="IIoService"/> returns.
/// </summary>
/// <remarks>
/// A failure travels in <see cref="Error"/> instead of an exception: when it is set the rest
/// of the payload is unset. The MCP serializer drops null members, so a failed call still
/// reaches the client as just <c>{ "error": "…" }</c>, exactly as before these results were typed.
/// </remarks>
public abstract record IoResult
{
    /// <summary>The error message, or <c>null</c> when the call succeeded.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="IIoService.LoadAsync"/> reports about an opened solution.</summary>
public sealed record LoadSolutionResult : IoResult
{
    /// <summary>File name of the solution that was opened, e.g. <c>MyApp.sln</c>.</summary>
    public string? Solution { get; init; }

    /// <summary>Absolute directory that contains the solution file.</summary>
    public string? Directory { get; init; }

    /// <summary>Number of projects in the solution.</summary>
    public int? ProjectCount { get; init; }

    /// <summary>Diagnostics MSBuildWorkspace produced while loading the projects.</summary>
    public IReadOnlyList<string>? WorkspaceDiagnostics { get; init; }

    /// <summary>The projects, in solution order.</summary>
    public IReadOnlyList<LoadedProject>? Projects { get; init; }
}

/// <summary>One project of a loaded solution, as <c>load_solution</c> lists it.</summary>
/// <param name="Name">The project name, unique within the solution.</param>
/// <param name="Path">Absolute path of the .csproj, or <c>null</c> for an in-memory project.</param>
/// <param name="Language">The Roslyn language name, e.g. <c>C#</c> or <c>Visual Basic</c>.</param>
/// <param name="AssemblyName">The assembly the project compiles to.</param>
/// <param name="DocumentCount">Documents in the project.</param>
/// <param name="ProjectReferenceCount">Number of project references.</param>
/// <param name="MetadataReferenceCount">Number of assembly references.</param>
public sealed record LoadedProject(
    string Name,
    string? Path,
    string Language,
    string AssemblyName,
    int DocumentCount,
    int ProjectReferenceCount,
    int MetadataReferenceCount);

/// <summary>What <see cref="IIoService.ListSolutions"/> found beneath a directory.</summary>
public sealed record ListSolutionsResult : IoResult
{
    /// <summary>Absolute root the scan started from.</summary>
    public string? RootPath { get; init; }

    /// <summary>Number of solution files found.</summary>
    public int? SolutionCount { get; init; }

    /// <summary>The solution files found, ordered by path.</summary>
    public IReadOnlyList<SolutionFileEntry>? Solutions { get; init; }
}

/// <summary>A solution file found by <see cref="IIoService.ListSolutions"/>.</summary>
/// <param name="Path">Absolute path of the .sln/.slnx file.</param>
/// <param name="RelativePath">The same path relative to the scan root.</param>
public sealed record SolutionFileEntry(string Path, string RelativePath);

/// <summary>What <see cref="IIoService.ReadFile"/> read.</summary>
public sealed record ReadFileResult : IoResult
{
    /// <summary>Absolute path of the file.</summary>
    public string? Path { get; init; }

    /// <summary>Total number of lines in the file, or <c>null</c> when the read failed.</summary>
    public int? TotalLines { get; init; }

    /// <summary>Number of lines returned — a range narrower than the file returns fewer — or <c>null</c> when the read failed.</summary>
    public int? ReturnedLines { get; init; }

    /// <summary>The requested lines, each prefixed with its 1-based number.</summary>
    public string? Content { get; init; }
}

/// <summary>What <see cref="IIoService.CheckSolution"/> reports about the workspace.</summary>
public sealed record CheckSolutionResult : IoResult
{
    /// <summary>Whether a solution with at least one C# or Visual Basic project is loaded.</summary>
    public bool IsLoaded { get; init; }

    /// <summary>Absolute path of the loaded solution, when there is one.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Number of C# projects in the loaded solution.</summary>
    [JsonPropertyName("csharpProjectCount")]
    public int? CSharpProjectCount { get; init; }

    /// <summary>Number of Visual Basic projects in the loaded solution.</summary>
    [JsonPropertyName("visualBasicProjectCount")]
    public int? VisualBasicProjectCount { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Message { get; init; }
}

/// <summary>What <see cref="IIoService.GetProjectInfoAsync"/> reports about one project.</summary>
public sealed record ProjectInfoResult : IoResult
{
    /// <summary>The project name, unique within the solution.</summary>
    public string? Name { get; init; }

    /// <summary>The assembly the project compiles to.</summary>
    public string? AssemblyName { get; init; }

    /// <summary>The Roslyn language name, e.g. <c>C#</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Absolute path of the .csproj.</summary>
    public string? FilePath { get; init; }

    /// <summary>The compilation output kind, e.g. <c>DynamicallyLinkedLibrary</c>.</summary>
    public string? OutputType { get; init; }

    /// <summary>Number of source files.</summary>
    public int? DocumentCount { get; init; }

    /// <summary>Absolute paths of the source files, ordered.</summary>
    public IReadOnlyList<string>? SourceFiles { get; init; }

    /// <summary>Names of the referenced projects, ordered.</summary>
    public IReadOnlyList<string>? ProjectReferences { get; init; }

    /// <summary>File names of the referenced assemblies, ordered.</summary>
    public IReadOnlyList<string>? AssemblyReferences { get; init; }

    /// <summary>Package references declared by the .csproj; empty when it cannot be parsed.</summary>
    public IReadOnlyList<PackageRef>? PackageReferences { get; init; }
}

/// <summary>
/// What <see cref="IIoService.ListDirectory"/> found: a directory inventory, a single file's
/// details, or the fact that the path does not exist.
/// </summary>
/// <remarks>
/// Which members carry meaning depends on <see cref="Exists"/> and <see cref="Type"/>:
/// a directory sets the match counts, <see cref="Subdirectories"/> and <see cref="Files"/>;
/// a file sets <see cref="SizeBytes"/>, <see cref="SizeMB"/> and <see cref="LastModified"/>.
/// </remarks>
public sealed record ListDirectoryResult : IoResult
{
    /// <summary>The path that was inspected.</summary>
    public string? Path { get; init; }

    /// <summary>Whether the path exists; <c>null</c> when the request itself was invalid.</summary>
    public bool? Exists { get; init; }

    /// <summary><c>file</c> or <c>directory</c>.</summary>
    public string? Type { get; init; }

    /// <summary>The glob the files were filtered with.</summary>
    public string? Pattern { get; init; }

    /// <summary>Whether subdirectories were recursed into.</summary>
    public bool? Recursive { get; init; }

    /// <summary>Number of files that matched the pattern and name filters.</summary>
    public int? MatchedFileCount { get; init; }

    /// <summary>Total size of the matched files, in bytes.</summary>
    public long? MatchedTotalSizeBytes { get; init; }

    /// <summary>Total size of the matched files, in megabytes.</summary>
    public double? MatchedTotalSizeMB { get; init; }

    /// <summary>Number of entries actually returned — capped by <c>maxEntries</c>.</summary>
    public int? ReturnedCount { get; init; }

    /// <summary>Whether <c>maxEntries</c> cut the list short.</summary>
    public bool? Truncated { get; init; }

    /// <summary>Size of the file, in bytes, when the path is a file.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Size of the file, in megabytes, when the path is a file.</summary>
    public double? SizeMB { get; init; }

    /// <summary>Last write time, formatted <c>yyyy-MM-dd HH:mm:ss</c>, when the path is a file.</summary>
    public string? LastModified { get; init; }

    /// <summary>The immediate subdirectories, when the listing did not recurse.</summary>
    public IReadOnlyList<string>? Subdirectories { get; init; }

    /// <summary>The files found, ordered by name.</summary>
    public IReadOnlyList<DirectoryFileEntry>? Files { get; init; }
}

/// <summary>A file listed by <see cref="IIoService.ListDirectory"/>.</summary>
/// <param name="Name">The file name, without directory.</param>
/// <param name="RelativePath">The path relative to the listed directory.</param>
/// <param name="SizeBytes">The file size, in bytes.</param>
/// <param name="SizeMB">The file size, in megabytes.</param>
/// <param name="LastModified">Last write time as <c>yyyy-MM-dd HH:mm:ss</c>, or <c>null</c> when it cannot be read.</param>
public sealed record DirectoryFileEntry(
    string Name,
    string RelativePath,
    long SizeBytes,
    double SizeMB,
    string? LastModified);

/// <summary>What <see cref="IIoService.MovePath"/> did.</summary>
public sealed record MovePathResult : IoResult
{
    /// <summary>Whether the move happened.</summary>
    public bool? Moved { get; init; }

    /// <summary><c>file</c> or <c>directory</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Absolute source path.</summary>
    public string? Source { get; init; }

    /// <summary>Absolute destination path.</summary>
    public string? Destination { get; init; }
}
