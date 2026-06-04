using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Packages;

[McpServerToolType]
public static class CheckNuGetUpdatesTool
{
    [McpServerTool(Name = "check_nuget_updates"), Description(
        "Find NuGet packages that have a newer version available ('dotnet list package --outdated'): " +
        "reports each outdated package with its resolved version and the latest available version, per " +
        "project. Use this to plan dependency upgrades. Queries the NuGet feed, so it needs network access " +
        "and a restored project. The solution auto-loads on first use.")]
    public static async Task<object> CheckNuGetUpdates(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Include pre-release versions as upgrade candidates (default: false).")] bool includePrerelease = false,
        [Description("Also report outdated transitive (indirect) dependencies (default: false).")] bool includeTransitive = false,
        [Description("Timeout in seconds (default: 180 — the NuGet feed can be slow).")] int timeoutSeconds = 180)
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
            .Select(p => new
            {
                p.Id,
                p.Project,
                p.Framework,
                resolved = p.Resolved,
                latest = p.Latest,
                p.Transitive,
            })
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
}
