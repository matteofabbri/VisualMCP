using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Packages;

[McpServerToolType]
public static class LocateNuGetPackageTool
{
    [McpServerTool(Name = "locate_nuget_package"), Description(
        "Locate a NuGet package in the local cache (~/.nuget/packages or $NUGET_PACKAGES) and list its " +
        "assemblies: the install path, the available cached versions, and — for a given version — the DLLs " +
        "grouped by target framework (lib/<tfm>). Use this INSTEAD OF a shell 'find/ls' over the NuGet cache " +
        "to find where a package's .dll lives. Read-only.")]
    public static object LocateNuGetPackage(
        [Description("The package id, e.g. 'Microsoft.FASTER.Core'.")] string packageId,
        [Description("Optional: a specific version (e.g. '2.6.5'). Omit to list the cached versions.")] string? version = null)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return new { error = "A package id is required." };

        var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        var pkgDir = Path.Combine(nugetRoot, packageId.Trim().ToLowerInvariant());
        if (!Directory.Exists(pkgDir))
            return new { packageId, nugetRoot, found = false, message = $"Package '{packageId}' is not in the NuGet cache." };

        var cachedVersions = Directory.EnumerateDirectories(pkgDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;

        if (string.IsNullOrWhiteSpace(version))
            return new
            {
                packageId,
                cacheDir = pkgDir,
                found = true,
                versionCount = cachedVersions.Count,
                versions = cachedVersions,
                note = "Pass a version to list its assemblies.",
            };

        var versionDir = Path.Combine(pkgDir, version.Trim());
        if (!Directory.Exists(versionDir))
            return new
            {
                packageId,
                requestedVersion = version,
                found = false,
                message = $"Version '{version}' is not cached.",
                availableVersions = cachedVersions,
            };

        // Group lib DLLs by target framework; also collect any other DLLs (e.g. runtimes/).
        var libFrameworks = new List<object>();
        var libDir = Path.Combine(versionDir, "lib");
        if (Directory.Exists(libDir))
        {
            foreach (var tfmDir in Directory.EnumerateDirectories(libDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var dlls = SafeDlls(tfmDir).Select(Path.GetFileName).Where(n => n is not null).ToList();
                if (dlls.Count > 0)
                    libFrameworks.Add(new { framework = Path.GetFileName(tfmDir), dlls });
            }
        }

        var allDlls = SafeDlls(versionDir, recursive: true)
            .Select(f => Path.GetRelativePath(versionDir, f))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            packageId,
            version,
            found = true,
            path = versionDir,
            libFrameworkCount = libFrameworks.Count,
            libFrameworks,
            dllCount = allDlls.Count,
            dlls = allDlls,
        };
    }

    private static IEnumerable<string> SafeDlls(string dir, bool recursive = false)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.dll", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
            });
        }
        catch { return Array.Empty<string>(); }
    }
}
