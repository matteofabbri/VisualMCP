using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Packages;

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
    public static Task<object> CheckNuGetVulnerabilities(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Also scan transitive (indirect) dependencies (default: true).")] bool includeTransitive = true,
        [Description("Timeout in seconds (default: 180 — the advisory feed can be slow).")] int timeoutSeconds = 180)
        => NuGetPackagesImpl.CheckVulnerabilitiesAsync(projectName, includeTransitive, timeoutSeconds);
}
