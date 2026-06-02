using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace VisualMCP.Workspace;

public sealed class RoslynWorkspaceService : IDisposable
{
    public static RoslynWorkspaceService Instance { get; } = new();

    private readonly SemaphoreSlim _sem = new(1, 1);
    private MSBuildWorkspace? _workspace;

    public Solution? CurrentSolution { get; private set; }
    public string? LoadedSolutionPath { get; private set; }

    private RoslynWorkspaceService() { }

    public async Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            _workspace?.Dispose();
            _workspace = MSBuildWorkspace.Create();

            var diagnostics = new List<string>();
            _workspace.WorkspaceFailed += (_, e) =>
                diagnostics.Add($"[{e.Diagnostic.Kind}] {e.Diagnostic.Message}");

            CurrentSolution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
            LoadedSolutionPath = solutionPath;

            return new SolutionLoadResult(CurrentSolution, diagnostics);
        }
        finally
        {
            _sem.Release();
        }
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _sem.Dispose();
    }
}

public record SolutionLoadResult(Solution Solution, IReadOnlyList<string> Diagnostics);
