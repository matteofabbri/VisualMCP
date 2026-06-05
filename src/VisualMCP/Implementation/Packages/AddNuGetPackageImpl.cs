using VisualMCP.Implementation.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Packages;

internal static class AddNuGetPackageImpl
{
    internal static async Task<object> RunAsync(string project, string packageId, string? version, bool prerelease, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(project)) return new { error = "A project name or .csproj path is required." };
        if (string.IsNullOrWhiteSpace(packageId)) return new { error = "A package id is required." };

        // Resolve the project to a .csproj path: by solution project name, or a direct path.
        string? csproj = null;
        var svc = RoslynWorkspaceService.Instance;
        var solution = await svc.EnsureSolutionLoadedAsync();
        if (solution is not null)
        {
            var proj = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));
            if (proj?.FilePath is not null) csproj = proj.FilePath;
        }
        if (csproj is null && File.Exists(project)) csproj = Path.GetFullPath(project);
        if (csproj is null)
            return new { error = $"Project '{project}' not found (not a solution project name nor an existing .csproj path)." };

        var dir = Path.GetDirectoryName(csproj)!;
        var args = $"add \"{csproj}\" package {packageId}"
            + (string.IsNullOrWhiteSpace(version) ? "" : $" --version {version}")
            + (prerelease ? " --prerelease" : "");

        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync("dotnet", args, dir, timeoutSeconds);
        if (timedOut) return new { error = $"dotnet add package timed out after {timeoutSeconds}s.", command = $"dotnet {args}" };

        var combined = (stdout + "\n" + stderr).Trim();
        if (exitCode != 0)
            return new { error = $"dotnet add package failed (exit {exitCode}).", command = $"dotnet {args}", output = ProcessRunner.Truncate(combined, 3000) };

        // Refresh the workspace so later tools see the new reference.
        if (solution is not null && svc.LoadedSolutionPath is { } sln)
            try { await svc.LoadSolutionAsync(sln); } catch { }

        return new
        {
            project = Path.GetFileNameWithoutExtension(csproj),
            csproj,
            packageId,
            version = string.IsNullOrWhiteSpace(version) ? "(latest)" : version,
            prerelease,
            succeeded = true,
            command = $"dotnet {args}",
            output = ProcessRunner.Truncate(combined, 3000),
        };
    }
}
