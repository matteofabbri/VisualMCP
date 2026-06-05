using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class UpdateNuGetPackageTool
{
    [McpServerTool(Name = "update_nuget_package"), Description(
        "Update a NuGet package in a project to a newer version (or the latest stable) via 'dotnet add " +
        "package', then reload the workspace. Omit version to go to the latest. Use this INSTEAD OF a shell " +
        "command. Pair with check_nuget_updates to see what's outdated.")]
    public static Task<object> UpdateNuGetPackage(
        [Description("Project: a solution project name or a path to the .csproj.")] string project,
        [Description("The NuGet package id to update.")] string packageId,
        [Description("Optional: target version. Omit for the latest stable.")] string? version = null,
        [Description("Allow a pre-release version (default: false).")] bool prerelease = false,
        [Description("Timeout in seconds (default: 180).")] int timeoutSeconds = 180)
        => NuGetManageImpl.UpdateAsync(project, packageId, version, prerelease, timeoutSeconds);
}
