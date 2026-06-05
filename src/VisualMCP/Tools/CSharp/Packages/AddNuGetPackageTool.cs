using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class AddNuGetPackageTool
{
    [McpServerTool(Name = "add_nuget_package"), Description(
        "Add a NuGet package to a project via 'dotnet add package' and restore, then reload the workspace " +
        "so later tools see the new reference. Use this INSTEAD OF a shell 'dotnet add package'. Identify " +
        "the project by its solution name or a .csproj path.")]
    public static Task<object> AddNuGetPackage(
        [Description("Project to add the package to: a solution project name or a path to the .csproj.")] string project,
        [Description("The NuGet package id, e.g. 'OpenCvSharp4'.")] string packageId,
        [Description("Optional: specific version. Omit for the latest stable.")] string? version = null,
        [Description("Allow a pre-release version (default: false).")] bool prerelease = false,
        [Description("Timeout in seconds (default: 180 — restore can hit the network).")] int timeoutSeconds = 180)
        => AddNuGetPackageImpl.RunAsync(project, packageId, version, prerelease, timeoutSeconds);
}
