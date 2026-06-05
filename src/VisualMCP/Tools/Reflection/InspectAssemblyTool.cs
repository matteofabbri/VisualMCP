using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Reflection;

namespace VisualMCP.Tools.Reflection;

[McpServerToolType]
public static class InspectAssemblyTool
{
    [McpServerTool(Name = "inspect_assembly"), Description(
        "Inspect a compiled .NET assembly (.dll) and list its PUBLIC API: exported types (class/interface/" +
        "struct/enum/delegate) with base type, interfaces and — by default — their members (constructors, " +
        "methods, properties, fields, events) with readable signatures. Reads metadata only (no code is " +
        "executed and the file is not locked). Use this INSTEAD OF a reflection 'LoadFrom' script or grepping " +
        "to discover a third-party library's API (e.g. a NuGet package's DLL found via locate_nuget_package). " +
        "Read-only.")]
    public static object InspectAssembly(
        [Description("Path to the .dll to inspect.")] string assemblyPath,
        [Description("Optional: only include types whose full name contains this substring (case-insensitive).")] string? typeFilter = null,
        [Description("Include each type's members (default: true). Set false for a types-only outline.")] bool includeMembers = true,
        [Description("Maximum number of types to return (default: 300).")] int maxTypes = 300)
        => InspectAssemblyImpl.Run(assemblyPath, typeFilter, includeMembers, maxTypes);
}
