using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class InspectPackagesTool
{
    [McpServerTool(Name = "inspect_packages"), Description(
        "Inspect SEVERAL NuGet packages in ONE call: for each one it locates the DLL in the local cache and returns its " +
        "public API, so you pass package ids instead of paths. Use this INSTEAD OF calling inspect_package once per " +
        "package when comparing what a set of libraries offers. Each entry is 'id' or 'id@version' (e.g. " +
        "'Newtonsoft.Json@13.0.3'); a package that is not cached or has no lib assembly is reported in place. Read-only.")]
    public static object InspectPackages(
        IPackageInspectionService pkg,
        [Description("Package ids, each 'id' or 'id@version'.")] string[] packages,
        [Description("Optional: only include types whose full name contains this substring (case-insensitive).")] string? typeFilter = null,
        [Description("Include each type's members (default: true).")] bool includeMembers = true,
        [Description("Maximum types per package (default: 120).")] int maxTypes = 120)
    {
        if (packages is null || packages.Length == 0)
            return new { error = "inspect_packages needs at least one package id." };

        var results = packages
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(entry =>
            {
                var parts = entry.Split('@', 2, StringSplitOptions.TrimEntries);
                var id = parts[0];
                var version = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                return new
                {
                    packageId = id,
                    version,
                    result = pkg.InspectPackage(id, version, null, typeFilter, includeMembers, maxTypes),
                };
            })
            .ToList();

        return new { count = results.Count, packages = results };
    }
}
