using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class RemoveNuGetPackageTool
{
    [McpServerTool(Name = "remove_nuget_package"), Description(
        "Remove a NuGet package reference from a project via 'dotnet remove package', then reload the " +
        "workspace. Use this INSTEAD OF a shell 'dotnet remove package'. Identify the project by its " +
        "solution name or a .csproj path.")]
    public static Task<object> RemoveNuGetPackage(
        [Description("Project to remove the package from: a solution project name or a path to the .csproj.")] string project,
        [Description("The NuGet package id to remove.")] string packageId,
        [Description("Timeout in seconds (default: 120).")] int timeoutSeconds = 120)
        => NuGetManageImpl.RemoveAsync(project, packageId, timeoutSeconds);
}
