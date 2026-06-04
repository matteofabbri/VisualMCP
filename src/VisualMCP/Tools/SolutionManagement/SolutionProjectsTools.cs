using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Tools.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.SolutionManagement;

internal static class SolutionHelper
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

    /// <summary>Reload the Roslyn workspace so later tools see the modified solution.</summary>
    internal static async Task ReloadAsync(string sln)
    {
        try { await RoslynWorkspaceService.Instance.LoadSolutionAsync(sln); } catch { /* best-effort */ }
    }
}

[McpServerToolType]
public static class AddProjectsToSolutionTool
{
    [McpServerTool(Name = "add_projects_to_solution"), Description(
        "Add one or more .csproj projects to a solution (.sln/.slnx) via 'dotnet sln add', then reload the " +
        "workspace so later tools see them. Use this INSTEAD OF a shell 'dotnet sln add'. Targets the loaded " +
        "solution unless solutionPath is given.")]
    public static async Task<object> AddProjectsToSolution(
        [Description("Paths to the .csproj files to add.")] string[] projectPaths,
        [Description("Optional: the .sln/.slnx to modify. Defaults to the loaded solution.")] string? solutionPath = null,
        [Description("Timeout in seconds (default: 60).")] int timeoutSeconds = 60)
    {
        if (projectPaths is null || projectPaths.Length == 0)
            return new { error = "Provide at least one .csproj path to add." };

        var (sln, error) = await SolutionHelper.ResolveSlnAsync(solutionPath);
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

        await SolutionHelper.ReloadAsync(sln!);
        var projects = await SolutionHelper.ListProjectsAsync(sln!);

        return new
        {
            solution = Path.GetFileName(sln),
            added = projectPaths,
            projectCount = projects.Count,
            projects,
            output = ProcessRunner.Truncate(stdout.Trim(), 2000),
        };
    }
}

[McpServerToolType]
public static class RemoveProjectsFromSolutionTool
{
    [McpServerTool(Name = "remove_projects_from_solution"), Description(
        "Remove one or more projects from a solution (.sln/.slnx) via 'dotnet sln remove' (this only " +
        "detaches them from the solution; it does NOT delete the project files), then reload the workspace. " +
        "Targets the loaded solution unless solutionPath is given.")]
    public static async Task<object> RemoveProjectsFromSolution(
        [Description("Paths to the .csproj files to remove from the solution.")] string[] projectPaths,
        [Description("Optional: the .sln/.slnx to modify. Defaults to the loaded solution.")] string? solutionPath = null,
        [Description("Timeout in seconds (default: 60).")] int timeoutSeconds = 60)
    {
        if (projectPaths is null || projectPaths.Length == 0)
            return new { error = "Provide at least one .csproj path to remove." };

        var (sln, error) = await SolutionHelper.ResolveSlnAsync(solutionPath);
        if (error is not null) return error;

        var dir = Path.GetDirectoryName(sln!)!;
        var args = $"sln \"{sln}\" remove " + string.Join(" ", projectPaths.Select(p => $"\"{p}\""));

        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync("dotnet", args, dir, timeoutSeconds);
        if (timedOut) return new { error = "dotnet sln remove timed out." };
        if (exitCode != 0)
            return new { error = $"dotnet sln remove failed: {stderr.Trim()}", output = ProcessRunner.Truncate(stdout, 2000), command = $"dotnet {args}" };

        await SolutionHelper.ReloadAsync(sln!);
        var projects = await SolutionHelper.ListProjectsAsync(sln!);

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
