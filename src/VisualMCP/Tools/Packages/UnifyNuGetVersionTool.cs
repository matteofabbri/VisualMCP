using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Packages;

namespace VisualMCP.Tools.Packages;

[McpServerToolType]
public static class UnifyNuGetVersionTool
{
    [McpServerTool(Name = "unify_nuget_version"), Description(
        "Resolve a NuGet VERSION CONFLICT: set the same version of a package across every project that " +
        "references it (top-level), via 'dotnet add package --version', then reload the workspace. Use " +
        "list_nuget_packages first to see the conflict and pick the target version. Review with git_diff " +
        "and rebuild afterwards.")]
    public static Task<object> UnifyNuGetVersion(
        [Description("The NuGet package id whose version to unify.")] string packageId,
        [Description("The single version to set everywhere, e.g. '13.0.3'.")] string version,
        [Description("Timeout in seconds per project (default: 120).")] int timeoutSeconds = 120)
        => NuGetManageImpl.UnifyVersionAsync(packageId, version, timeoutSeconds);
}
