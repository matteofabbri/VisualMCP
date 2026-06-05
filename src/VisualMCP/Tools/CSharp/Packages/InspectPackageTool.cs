using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class InspectPackageTool
{
    [McpServerTool(Name = "inspect_package"), Description(
        "One-shot: locate a NuGet package's assembly in the local cache AND inspect its public API (types, " +
        "members, signatures). Combines locate_nuget_package + inspect_assembly so you pass a package id + " +
        "version instead of finding the .dll path yourself. Picks the best lib target framework automatically. " +
        "Read-only.")]
    public static object InspectPackage(
        [Description("The package id, e.g. 'Newtonsoft.Json'.")] string packageId,
        [Description("Optional: version (e.g. '13.0.3'). Omit for the highest cached version.")] string? version = null,
        [Description("Optional: target framework folder to pick (e.g. 'net8.0'). Omit to auto-pick the best.")] string? targetFramework = null,
        [Description("Optional: only include types whose full name contains this substring (case-insensitive).")] string? typeFilter = null,
        [Description("Include each type's members (default: true).")] bool includeMembers = true,
        [Description("Maximum number of types to return (default: 300).")] int maxTypes = 300)
        => PackageInspectionImpl.InspectPackage(packageId, version, targetFramework, typeFilter, includeMembers, maxTypes);
}
