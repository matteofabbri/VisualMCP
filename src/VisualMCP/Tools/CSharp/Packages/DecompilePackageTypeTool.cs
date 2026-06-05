using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class DecompilePackageTypeTool
{
    [McpServerTool(Name = "decompile_package_type"), Description(
        "One-shot: locate a NuGet package's assembly in the local cache AND decompile one type from it to C# " +
        "(ILSpy engine). Combines locate_nuget_package + decompile_type so you pass a package id + version + " +
        "type name instead of finding the .dll path yourself. Picks the best lib target framework automatically. " +
        "Read-only.")]
    public static object DecompilePackageType(
        [Description("The package id, e.g. 'Newtonsoft.Json'.")] string packageId,
        [Description("Fully-qualified type name, e.g. 'Newtonsoft.Json.JsonConvert'.")] string typeFullName,
        [Description("Optional: version (e.g. '13.0.3'). Omit for the highest cached version.")] string? version = null,
        [Description("Optional: target framework folder to pick (e.g. 'net8.0'). Omit to auto-pick the best.")] string? targetFramework = null,
        [Description("Maximum characters of C# to return (default: 60000).")] int maxChars = 60000)
        => PackageInspectionImpl.DecompilePackageType(packageId, version, typeFullName, targetFramework, maxChars);
}
