namespace VisualMCP.Implementation.CSharp.Packages;

/// <summary>Implementation for the NuGet package MCP commands.</summary>
internal static class NuGetPackagesImpl
{
    internal static async Task<object> ListAsync(string? projectName, bool includeTransitive, int timeoutSeconds)
    {
        var (target, targetErr) = await NuGetCli.ResolveTargetAsync(projectName);
        if (targetErr is not null) return targetErr;

        var (packages, error) = await NuGetCli.ListAsync(
            target!, includeTransitive ? "--include-transitive" : "", timeoutSeconds);
        if (error is not null) return error;

        var conflicts = packages!
            .Where(p => !p.Transitive && p.Resolved is not null)
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Resolved).Distinct().Count() > 1)
            .Select(g => new
            {
                id = g.Key,
                versions = g.GroupBy(x => x.Resolved)
                    .Select(vg => new { version = vg.Key, projects = vg.Select(x => x.Project).Distinct().ToList() })
                    .ToList(),
            })
            .ToList();

        return new
        {
            target = Path.GetFileName(target),
            projectFilter = projectName ?? "all",
            packageCount = packages!.Count,
            conflictCount = conflicts.Count,
            conflicts,
            packages = packages.Select(p => new { p.Project, p.Framework, p.Id, requested = p.Requested, resolved = p.Resolved, p.Transitive }),
        };
    }

    internal static async Task<object> CheckUpdatesAsync(string? projectName, bool includePrerelease, bool includeTransitive, int timeoutSeconds)
    {
        var (target, targetErr) = await NuGetCli.ResolveTargetAsync(projectName);
        if (targetErr is not null) return targetErr;

        var flags = "--outdated"
            + (includePrerelease ? " --include-prerelease" : "")
            + (includeTransitive ? " --include-transitive" : "");

        var (packages, error) = await NuGetCli.ListAsync(target!, flags, timeoutSeconds);
        if (error is not null) return error;

        var updates = packages!
            .Where(p => p.Latest is not null && p.Latest != p.Resolved)
            .Select(p => new { p.Id, p.Project, p.Framework, resolved = p.Resolved, latest = p.Latest, p.Transitive })
            .OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            target = Path.GetFileName(target),
            projectFilter = projectName ?? "all",
            includePrerelease,
            outdatedCount = updates.Count,
            updates,
            note = updates.Count == 0 ? "All packages are up to date (for the selected scope)." : null,
        };
    }

    internal static async Task<object> CheckVulnerabilitiesAsync(string? projectName, bool includeTransitive, int timeoutSeconds)
    {
        var (target, targetErr) = await NuGetCli.ResolveTargetAsync(projectName);
        if (targetErr is not null) return targetErr;

        var flags = "--vulnerable" + (includeTransitive ? " --include-transitive" : "");

        var (packages, error) = await NuGetCli.ListAsync(target!, flags, timeoutSeconds);
        if (error is not null) return error;

        var vulnerable = packages!
            .Where(p => p.Vulnerabilities.Count > 0)
            .Select(p => new { p.Id, p.Project, p.Framework, resolved = p.Resolved, p.Transitive, vulnerabilities = p.Vulnerabilities })
            .ToList();

        var bySeverity = vulnerable
            .SelectMany(v => v.vulnerabilities)
            .GroupBy(v => (string?)((dynamic)v).severity ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            target = Path.GetFileName(target),
            projectFilter = projectName ?? "all",
            includeTransitive,
            vulnerablePackageCount = vulnerable.Count,
            advisoryCount = vulnerable.Sum(v => v.vulnerabilities.Count),
            bySeverity,
            vulnerabilities = vulnerable,
            note = vulnerable.Count == 0 ? "No known vulnerabilities found (for the selected scope)." : null,
        };
    }

    internal static object Locate(string packageId, string? version)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return new { error = "A package id is required." };

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var pkgDir = Path.Combine(nugetRoot, packageId.Trim().ToLowerInvariant());
        if (!Directory.Exists(pkgDir))
            return new { packageId, nugetRoot, found = false, message = $"Package '{packageId}' is not in the NuGet cache." };

        var cachedVersions = Directory.EnumerateDirectories(pkgDir)
            .Select(Path.GetFileName).Where(n => n is not null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()!;

        if (string.IsNullOrWhiteSpace(version))
            return new { packageId, cacheDir = pkgDir, found = true, versionCount = cachedVersions.Count, versions = cachedVersions, note = "Pass a version to list its assemblies." };

        var versionDir = Path.Combine(pkgDir, version.Trim());
        if (!Directory.Exists(versionDir))
            return new { packageId, requestedVersion = version, found = false, message = $"Version '{version}' is not cached.", availableVersions = cachedVersions };

        var libFrameworks = new List<object>();
        var libDir = Path.Combine(versionDir, "lib");
        if (Directory.Exists(libDir))
            foreach (var tfmDir in Directory.EnumerateDirectories(libDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var dlls = SafeDlls(tfmDir).Select(Path.GetFileName).Where(n => n is not null).ToList();
                if (dlls.Count > 0) libFrameworks.Add(new { framework = Path.GetFileName(tfmDir), dlls });
            }

        var allDlls = SafeDlls(versionDir, recursive: true)
            .Select(f => Path.GetRelativePath(versionDir, f))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        return new
        {
            packageId, version, found = true, path = versionDir,
            libFrameworkCount = libFrameworks.Count, libFrameworks,
            dllCount = allDlls.Count, dlls = allDlls,
        };
    }

    private static IEnumerable<string> SafeDlls(string dir, bool recursive = false)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }); }
        catch { return Array.Empty<string>(); }
    }
}
