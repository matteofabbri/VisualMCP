using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Workspace;

/// <summary>
/// The "active solution" view that tools operate on. Implemented by
/// <see cref="Snitch.SnitchContext"/>, which owns the opened environment (all discovered
/// solutions) and exposes the active one here. Inject this interface rather than reaching
/// for a global instance.
/// </summary>
public interface IRoslynWorkspace : IDisposable
{
    /// <summary>The active solution, or null if none is loaded yet.</summary>
    Solution? CurrentSolution { get; }

    /// <summary>Absolute path of the active solution, or null.</summary>
    string? LoadedSolutionPath { get; }

    /// <summary>
    /// Returns the active solution, opening the environment from the working directory if
    /// nothing is loaded yet. Returns null when no unambiguous solution can be found.
    /// </summary>
    Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default);

    /// <summary>Loads the given solution file and makes it the active solution.</summary>
    Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default);

    /// <summary>Heuristically locates a solution file from the working directory, or null.</summary>
    string? DiscoverSolutionPath();

    /// <summary>
    /// Replaces one document's text in the active solution. A tool that rewrites a file on disk
    /// calls this so the model matches the file: without it, a second refactoring on the same file
    /// reads the pre-write text and undoes the first. Returns false when the document is not part
    /// of the active solution.
    /// </summary>
    bool TryUpdateDocument(DocumentId documentId, SourceText text);

    /// <summary>
    /// Adds a document the model does not know yet — a file a tool just created — so the following
    /// calls see it without reloading the solution. Returns the new document, or null when the
    /// project is unknown or already has a document at that path.
    /// </summary>
    DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text);

    /// <summary>
    /// Removes a document the tool has just moved or deleted from disk, so the model stops reporting
    /// a file that is no longer there. Returns false when the document is not part of the active
    /// solution.
    /// </summary>
    bool TryRemoveDocument(DocumentId documentId);
}
