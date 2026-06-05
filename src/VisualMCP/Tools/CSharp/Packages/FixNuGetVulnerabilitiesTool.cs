using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class FixNuGetVulnerabilitiesTool
{
    [McpServerTool(Name = "fix_nuget_vulnerabilities"), Description(
        "Find vulnerable NuGet packages (dotnet list package --vulnerable) and AUTO-UPDATE the affected " +
        "top-level packages to the latest stable version to clear the advisories. Transitive advisories are " +
        "reported for manual attention (update the direct dependency that pulls them in). Then reloads the " +
        "workspace. Review the changes (git_diff) and rebuild afterwards.")]
    public static Task<object> FixNuGetVulnerabilities(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Timeout in seconds per package update (default: 180).")] int timeoutSeconds = 180)
        => NuGetManageImpl.FixVulnerabilitiesAsync(projectName, timeoutSeconds);
}
