using VisualMCP.Implementation.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.CSharp.SolutionManagement;

/// <summary>Implementation for the solution-project management MCP commands.</summary>
internal static class SolutionProjectsImpl
{
    internal static async Task<(string? sln, object? error)> ResolveSlnAsync(string? solutionPath)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            if (!File.Exists(solutionPath)) return (null, new { error = $"Solution file not found: {solutionPath}" });
            return (Path.GetFullPath(solutionPath), null);
        }

        var svc = RoslynWorkspaceService.Instance;
        await svc.EnsureSolutionLoadedAsync();
        var sln = svc.LoadedSolutionPath;
        if (sln is null)
            return (null, new { error = "No solution loaded or auto-located. Pass solutionPath, or open a .sln/.slnx first." });
        return (sln, null);
    }

    internal static async Task<List<string>> ListProjectsAsync(string sln)
    {
        var dir = Path.GetDirectoryName(sln)!;
        var (_, _, stdout, _, _) = await ProcessRunner.RunAsync("dotnet", $"sln \"{sln}\" list", dir, 30);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0
                        && !l.StartsWith("Project", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("---", StringComparison.Ordinal))
            .ToList();
    }

    private static async Task ReloadAsync(string sln)
    {
        try { await RoslynWorkspaceService.Instance.LoadSolutionAsync(sln); } catch { /* best-effort */ }
    }

    internal static async Task<object> AddAsync(string[] projectPaths, string? solutionPath, int timeoutSeconds)
    {
        if (projectPaths is null || projectPaths.Length == 0)
            return new { error = "Provide at least one .csproj path to add." };

        var (sln, error) = await ResolveSlnAsync(solutionPath);
        if (error is not null) return error;

        var missing = projectPaths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            return new { error = $"Project file(s) not found: {string.Join(", ", missing)}" };

        var dir = Path.GetDirectoryName(sln!)!;
        var args = $"sln \"{sln}\" add " + string.Join(" ", projectPaths.Select(p => $"\"{p}\""));

        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync("dotnet", args, dir, timeoutSeconds);
        if (timedOut) return new { error = "dotnet sln add timed out." };
        if (exitCode != 0)
            return new { error = $"dotnet sln add failed: {stderr.Trim()}", output = ProcessRunner.Truncate(stdout, 2000), command = $"dotnet {args}" };

        await ReloadAsync(sln!);
        var projects = await ListProjectsAsync(sln!);

        return new
        {
            solution = Path.GetFileName(sln),
            added = projectPaths,
            projectCount = projects.Count,
            projects,
            output = ProcessRunner.Truncate(stdout.Trim(), 2000),
        };
    }

    internal static async Task<object> RemoveAsync(string[] projectPaths, string? solutionPath, int timeoutSeconds)
    {
        if (projectPaths is null || projectPaths.Length == 0)
            return new { error = "Provide at least one .csproj path to remove." };

        var (sln, error) = await ResolveSlnAsync(solutionPath);
        if (error is not null) return error;

        var dir = Path.GetDirectoryName(sln!)!;
        var args = $"sln \"{sln}\" remove " + string.Join(" ", projectPaths.Select(p => $"\"{p}\""));

        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync("dotnet", args, dir, timeoutSeconds);
        if (timedOut) return new { error = "dotnet sln remove timed out." };
        if (exitCode != 0)
            return new { error = $"dotnet sln remove failed: {stderr.Trim()}", output = ProcessRunner.Truncate(stdout, 2000), command = $"dotnet {args}" };

        await ReloadAsync(sln!);
        var projects = await ListProjectsAsync(sln!);

        return new
        {
            solution = Path.GetFileName(sln),
            removed = projectPaths,
            projectCount = projects.Count,
            projects,
            output = ProcessRunner.Truncate(stdout.Trim(), 2000),
        };
    }
}
