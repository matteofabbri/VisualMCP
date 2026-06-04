using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Packages;

[McpServerToolType]
public static class CheckNuGetVulnerabilitiesTool
{
    [McpServerTool(Name = "check_nuget_vulnerabilities"), Description(
        "Find NuGet packages with known security vulnerabilities ('dotnet list package --vulnerable'): " +
        "reports each affected package with its resolved version and the advisory severity + URL (GHSA), " +
        "per project. Includes transitive dependencies by default, since most advisories hit indirect " +
        "packages. Use this to triage supply-chain risk. Queries the NuGet/GitHub advisory feed, so it " +
        "needs network access and a restored project. The solution auto-loads on first use.")]
    public static async Task<object> CheckNuGetVulnerabilities(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Also scan transitive (indirect) dependencies (default: true).")] bool includeTransitive = true,
        [Description("Timeout in seconds (default: 180 — the advisory feed can be slow).")] int timeoutSeconds = 180)
    {
        var (target, targetErr) = await NuGetCli.ResolveTargetAsync(projectName);
        if (targetErr is not null) return targetErr;

        var flags = "--vulnerable" + (includeTransitive ? " --include-transitive" : "");

        var (packages, error) = await NuGetCli.ListAsync(target!, flags, timeoutSeconds);
        if (error is not null) return error;

        var vulnerable = packages!
            .Where(p => p.Vulnerabilities.Count > 0)
            .Select(p => new
            {
                p.Id,
                p.Project,
                p.Framework,
                resolved = p.Resolved,
                p.Transitive,
                vulnerabilities = p.Vulnerabilities,
            })
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
}
