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

    /// <summary>
    /// Returns the loaded solution, auto-discovering and loading one from the
    /// current working directory if none has been loaded yet. This lets tools
    /// "just work" on the first call without the agent having to call
    /// load_solution explicitly. Returns null only when no unambiguous solution
    /// can be found (in which case the caller should fall back to load_solution).
    /// </summary>
    public async Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default)
    {
        if (CurrentSolution is not null)
            return CurrentSolution;

        var path = DiscoverSolutionPath();
        if (path is null)
            return null;

        try
        {
            var result = await LoadSolutionAsync(path, ct);
            return result.Solution;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Heuristically locates a single solution file relative to the process's
    /// current working directory: first scanning downward (depth-limited), then
    /// walking up the parent chain. When several are found, the shallowest path
    /// wins. Returns null when nothing is found.
    /// </summary>
    public static string? DiscoverSolutionPath()
    {
        var root = Directory.GetCurrentDirectory();

        var found = SafeEnumerate(root, maxDepth: 4).ToList();

        if (found.Count == 0)
        {
            var dir = Directory.GetParent(root);
            while (dir is not null && found.Count == 0)
            {
                found = dir.GetFiles("*.sln")
                    .Concat(dir.GetFiles("*.slnx"))
                    .Select(f => f.FullName)
                    .ToList();
                dir = dir.Parent;
            }
        }

        if (found.Count == 0) return null;

        // Prefer the shallowest path (fewest separators), then shortest name —
        // a repo's top-level solution rather than a nested fixture/sample.
        return found
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(p => p.Length)
            .First();
    }

    private static IEnumerable<string> SafeEnumerate(string dir, int maxDepth, int depth = 0)
    {
        if (depth > maxDepth) yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.sln")
                .Concat(Directory.GetFiles(dir, "*.slnx"))
                .ToArray();
        }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var f in files) yield return f;

        string[] subDirs;
        try { subDirs = Directory.GetDirectories(dir); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var sub in subDirs)
        {
            // Skip noise that never contains the solution we want.
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or ".git" or "node_modules") continue;

            foreach (var f in SafeEnumerate(sub, maxDepth, depth + 1))
                yield return f;
        }
    }

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

            // Pre-index the solution's Markdown/documentation so it is ready to read.
            ProjectDocsService.WarmInBackground(Path.GetDirectoryName(solutionPath));

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
