using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// Resolves the project a file-under-the-project tool has to write to: an explicit path (a .csproj or
/// its folder), a solution project name, or the single project of the solution.
/// </summary>
internal static class ProjectLocation
{
    public static async Task<(string? Directory, object? Error)> ResolveDirectoryAsync(
        IRoslynWorkspace workspace, string? projectName, string? projectPath)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var full = Path.GetFullPath(projectPath);
            if (File.Exists(full)) return (Path.GetDirectoryName(full), null);
            if (Directory.Exists(full)) return (full, null);
            return (null, new { error = $"'{projectPath}' does not exist." });
        }

        var solution = await workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return (null, new { error = "No C# solution could be auto-located. Call load_solution with an explicit path, or pass projectPath." });

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var named = solution.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (named?.FilePath is null)
                return (null, new { error = $"Project '{projectName}' not found in the solution.", knownProjects = solution.Projects.Select(project => project.Name) });

            return (Path.GetDirectoryName(named.FilePath), null);
        }

        var withPath = solution.Projects.Where(project => project.FilePath is not null).ToList();
        return withPath.Count == 1
            ? (Path.GetDirectoryName(withPath[0].FilePath!), null)
            : (null, new { error = "The solution has more than one project; pass projectName (or projectPath).", knownProjects = withPath.Select(project => project.Name) });
    }
}
