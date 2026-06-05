using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class CheckNuGetUpdatesTool
{
    [McpServerTool(Name = "check_nuget_updates"), Description(
        "Find NuGet packages that have a newer version available ('dotnet list package --outdated'): " +
        "reports each outdated package with its resolved version and the latest available version, per " +
        "project. Use this to plan dependency upgrades. Queries the NuGet feed, so it needs network access " +
        "and a restored project. The solution auto-loads on first use.")]
    public static Task<object> CheckNuGetUpdates(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Include pre-release versions as upgrade candidates (default: false).")] bool includePrerelease = false,
        [Description("Also report outdated transitive (indirect) dependencies (default: false).")] bool includeTransitive = false,
        [Description("Timeout in seconds (default: 180 — the NuGet feed can be slow).")] int timeoutSeconds = 180)
        => NuGetPackagesImpl.CheckUpdatesAsync(projectName, includePrerelease, includeTransitive, timeoutSeconds);
}
