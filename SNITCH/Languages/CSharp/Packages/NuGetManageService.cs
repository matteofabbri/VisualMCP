using Snitch.Execution;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Packages;

/// <summary>Add/remove/update NuGet packages and resolve conflicts/vulnerabilities.</summary>
public interface INuGetManageService
{
    Task<object> RemoveAsync(string project, string packageId, int timeoutSeconds);
    Task<object> UpdateAsync(string project, string packageId, string? version, bool prerelease, int timeoutSeconds);
    Task<object> FixVulnerabilitiesAsync(string? projectName, int timeoutSeconds);
    Task<object> UnifyVersionAsync(string packageId, string version, int timeoutSeconds);
}

public sealed class NuGetManageService : INuGetManageService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IAddNuGetPackageService _add;

    public NuGetManageService(IRoslynWorkspace workspace, IAddNuGetPackageService add)
    {
        _workspace = workspace;
        _add = add;
    }

    // ── remove ────────────────────────────────────────────────────────────────
    public async Task<object> RemoveAsync(string project, string packageId, int timeoutSeconds)
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
    public Task<object> UpdateAsync(string project, string packageId, string? version, bool prerelease, int timeoutSeconds)
        => _add.RunAsync(project, packageId, version, prerelease, timeoutSeconds);

    // ── fix vulnerabilities (update affected top-level packages to latest) ──────
    public async Task<object> FixVulnerabilitiesAsync(string? projectName, int timeoutSeconds)
    {
        var (target, terr) = await NuGetCli.ResolveTargetAsync(_workspace, projectName);
        if (terr is not null) return terr;

        var (packages, lerr) = await NuGetCli.ListAsync(target!, "--vulnerable --include-transitive", Math.Min(timeoutSeconds, 180));
        if (lerr is not null) return lerr;

        var vulnerable = packages!.Where(p => p.Vulnerabilities.Count > 0).ToList();
        if (vulnerable.Count == 0) return new { target = Path.GetFileName(target), fixedCount = 0, note = "No vulnerable packages found." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();

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
    public async Task<object> UnifyVersionAsync(string packageId, string version, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return new { error = "A package id is required." };
        if (string.IsNullOrWhiteSpace(version)) return new { error = "A target version is required." };

        var (target, terr) = await NuGetCli.ResolveTargetAsync(_workspace, null);
        if (terr is not null) return terr;

        var (packages, lerr) = await NuGetCli.ListAsync(target!, "", Math.Min(timeoutSeconds, 180));
        if (lerr is not null) return lerr;

        var solution = await _workspace.EnsureSolutionLoadedAsync();

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
    private async Task<(string? csproj, object? error)> ResolveCsprojAsync(string project)
    {
        if (string.IsNullOrWhiteSpace(project))
            return (null, new { error = "A project name or .csproj path is required." });

        var svc = _workspace;
        var solution = await svc.EnsureSolutionLoadedAsync();
        if (solution is not null)
        {
            var p = solution.Projects.FirstOrDefault(x => string.Equals(x.Name, project, StringComparison.OrdinalIgnoreCase));
            if (p?.FilePath is not null) return (p.FilePath, null);
        }
        if (File.Exists(project)) return (Path.GetFullPath(project), null);
        return (null, new { error = $"Project '{project}' not found (not a solution project name nor an existing .csproj path)." });
    }

    private async Task ReloadAsync()
    {
        var svc = _workspace;
        if (svc.LoadedSolutionPath is { } sln) try { await svc.LoadSolutionAsync(sln); } catch { }
    }
}
