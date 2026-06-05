using VisualMCP.Implementation.Execution;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.CSharp.Packages;

/// <summary>Add/remove/update NuGet packages and resolve conflicts/vulnerabilities.</summary>
internal static class NuGetManageImpl
{
    // ── remove ────────────────────────────────────────────────────────────────
    internal static async Task<object> RemoveAsync(string project, string packageId, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return new { error = "A package id is required." };
        var (csproj, error) = await ResolveCsprojAsync(project);
        if (error is not null) return error;

        var args = $"remove \"{csproj}\" package {packageId}";
        var (exit, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(csproj!)!, timeoutSeconds);
        if (timedOut) return new { error = "dotnet remove package timed out.", command = $"dotnet {args}" };
        var combined = (stdout + "\n" + stderr).Trim();
        if (exit != 0) return new { error = $"dotnet remove package failed (exit {exit}).", command = $"dotnet {args}", output = ProcessRunner.Truncate(combined, 2000) };

        await ReloadAsync();
        return new { project = Path.GetFileNameWithoutExtension(csproj), packageId, removed = true, command = $"dotnet {args}", output = ProcessRunner.Truncate(combined, 2000) };
    }

    // ── update (= dotnet add to latest / specific version) ─────────────────────
    internal static Task<object> UpdateAsync(string project, string packageId, string? version, bool prerelease, int timeoutSeconds)
        => AddNuGetPackageImpl.RunAsync(project, packageId, version, prerelease, timeoutSeconds);

    // ── fix vulnerabilities (update affected top-level packages to latest) ──────
    internal static async Task<object> FixVulnerabilitiesAsync(string? projectName, int timeoutSeconds)
    {
        var (target, terr) = await NuGetCli.ResolveTargetAsync(projectName);
        if (terr is not null) return terr;

        var (packages, lerr) = await NuGetCli.ListAsync(target!, "--vulnerable --include-transitive", Math.Min(timeoutSeconds, 180));
        if (lerr is not null) return lerr;

        var vulnerable = packages!.Where(p => p.Vulnerabilities.Count > 0).ToList();
        if (vulnerable.Count == 0) return new { target = Path.GetFileName(target), fixedCount = 0, note = "No vulnerable packages found." };

        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();

        var updated = new List<object>();
        var manual = new List<object>();

        foreach (var v in vulnerable)
        {
            if (v.Transitive)
            {
                manual.Add(new { v.Id, v.Project, resolved = v.Resolved, reason = "transitive — update the top-level package that pulls it in" });
                continue;
            }

            var csproj = solution?.Projects.FirstOrDefault(p => p.Name == v.Project)?.FilePath;
            if (csproj is null) { manual.Add(new { v.Id, v.Project, reason = "could not resolve project path" }); continue; }

            var args = $"add \"{csproj}\" package {v.Id}";   // no version => latest stable
            var (exit, _, so, se, _) = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(csproj)!, timeoutSeconds);
            if (exit == 0) updated.Add(new { v.Id, v.Project, from = v.Resolved, action = "updated to latest" });
            else manual.Add(new { v.Id, v.Project, reason = $"update failed: {(se + so).Trim()}" });
        }

        await ReloadAsync();
        return new
        {
            target = Path.GetFileName(target),
            vulnerableFound = vulnerable.Count,
            fixedCount = updated.Count,
            updated,
            needsManualAttention = manual,
            note = manual.Count > 0 ? "Some advisories are transitive — update the direct dependency that brings them in." : null,
        };
    }

    // ── unify version across projects (resolve a version conflict) ──────────────
    internal static async Task<object> UnifyVersionAsync(string packageId, string version, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return new { error = "A package id is required." };
        if (string.IsNullOrWhiteSpace(version)) return new { error = "A target version is required." };

        var (target, terr) = await NuGetCli.ResolveTargetAsync(null);
        if (terr is not null) return terr;

        var (packages, lerr) = await NuGetCli.ListAsync(target!, "", Math.Min(timeoutSeconds, 180));
        if (lerr is not null) return lerr;

        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();

        var projectsWithPkg = packages!
            .Where(p => !p.Transitive && p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Project).Distinct().ToList();

        if (projectsWithPkg.Count == 0)
            return new { error = $"No project references top-level package '{packageId}'." };

        var results = new List<object>();
        foreach (var projName in projectsWithPkg)
        {
            var csproj = solution?.Projects.FirstOrDefault(p => p.Name == projName)?.FilePath;
            if (csproj is null) { results.Add(new { project = projName, action = "skipped (no path)" }); continue; }

            var args = $"add \"{csproj}\" package {packageId} --version {version}";
            var (exit, _, so, se, _) = await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(csproj)!, timeoutSeconds);
            results.Add(new { project = projName, action = exit == 0 ? $"set to {version}" : $"failed: {(se + so).Trim()}" });
        }

        await ReloadAsync();
        return new { packageId, version, projectsUpdated = results.Count, results };
    }

    // ── helpers ────────────────────────────────────────────────────────────────
    private static async Task<(string? csproj, object? error)> ResolveCsprojAsync(string project)
    {
        if (string.IsNullOrWhiteSpace(project))
            return (null, new { error = "A project name or .csproj path is required." });

        var svc = RoslynWorkspaceService.Instance;
        var solution = await svc.EnsureSolutionLoadedAsync();
        if (solution is not null)
        {
            var p = solution.Projects.FirstOrDefault(x => string.Equals(x.Name, project, StringComparison.OrdinalIgnoreCase));
            if (p?.FilePath is not null) return (p.FilePath, null);
        }
        if (File.Exists(project)) return (Path.GetFullPath(project), null);
        return (null, new { error = $"Project '{project}' not found (not a solution project name nor an existing .csproj path)." });
    }

    private static async Task ReloadAsync()
    {
        var svc = RoslynWorkspaceService.Instance;
        if (svc.LoadedSolutionPath is { } sln) try { await svc.LoadSolutionAsync(sln); } catch { }
    }
}
