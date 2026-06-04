using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Packages;

[McpServerToolType]
public static class ListNuGetPackagesTool
{
    [McpServerTool(Name = "list_nuget_packages"), Description(
        "List the NuGet packages of the solution (or one project) with their requested and resolved " +
        "versions per target framework, and detect VERSION CONFLICTS — the same package pinned to " +
        "different resolved versions across projects. Use this INSTEAD OF reading .csproj files by hand: " +
        "it reflects the real restored graph from 'dotnet list package'. Set includeTransitive=true to " +
        "also list indirect dependencies. The solution auto-loads on first use; the project must be restored " +
        "(run build_project once if it reports it needs restoring).")]
    public static async Task<object> ListNuGetPackages(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Also include transitive (indirect) packages (default: false).")] bool includeTransitive = false,
        [Description("Timeout in seconds (default: 120).")] int timeoutSeconds = 120)
    {
        var (target, targetErr) = await NuGetCli.ResolveTargetAsync(projectName);
        if (targetErr is not null) return targetErr;

        var (packages, error) = await NuGetCli.ListAsync(
            target!, includeTransitive ? "--include-transitive" : "", timeoutSeconds);
        if (error is not null) return error;

        // Version conflicts: a top-level package resolved to >1 distinct version across projects.
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
            packages = packages.Select(p => new
            {
                p.Project,
                p.Framework,
                p.Id,
                requested = p.Requested,
                resolved = p.Resolved,
                p.Transitive,
            }),
        };
    }
}
