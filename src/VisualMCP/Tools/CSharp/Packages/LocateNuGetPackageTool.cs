using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class LocateNuGetPackageTool
{
    [McpServerTool(Name = "locate_nuget_package"), Description(
        "Locate a NuGet package in the local cache (~/.nuget/packages or $NUGET_PACKAGES) and list its " +
        "assemblies: the install path, the available cached versions, and — for a given version — the DLLs " +
        "grouped by target framework (lib/<tfm>). Use this INSTEAD OF a shell 'find/ls' over the NuGet cache " +
        "to find where a package's .dll lives. Read-only.")]
    public static object LocateNuGetPackage(
        [Description("The package id, e.g. 'Microsoft.FASTER.Core'.")] string packageId,
        [Description("Optional: a specific version (e.g. '2.6.5'). Omit to list the cached versions.")] string? version = null)
        => NuGetPackagesImpl.Locate(packageId, version);
}
