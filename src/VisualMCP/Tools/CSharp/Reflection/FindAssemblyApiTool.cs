using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Reflection;

namespace VisualMCP.Tools.CSharp.Reflection;

[McpServerToolType]
public static class FindAssemblyApiTool
{
    [McpServerTool(Name = "find_assembly_api"), Description(
        "Search a compiled .NET assembly's API by REGEX over type and member names — one call instead of " +
        "inspect_assembly once per type name. A type whose name matches comes back with all its members (so it " +
        "replaces inspect_assembly(typeFilter: thatName)); a member whose name matches comes back as a single " +
        "signature line under its type (matchedBy='member'), so 'which methods mention Parse' is one call. " +
        "Reads metadata only: nothing is loaded, executed or locked. Find the DLL with locate_nuget_package or " +
        "inspect_package first. Read-only.")]
    public static object FindAssemblyApi(
        IInspectAssemblyService inspect,
        [Description("Path to the .dll/.exe to search.")] string assemblyPath,
        [Description("Regex over type full names and member names (case-insensitive), e.g. 'Syntax$|Token'.")] string pattern,
        [Description("Optional: class|interface|struct|enum|delegate (restrict the type kind) or constructor|method|property|field|event (only that member kind).")] string? kind = null,
        [Description("Maximum number of matching types to return (default 60, max 2000).")] int maxMatches = 60)
        => inspect.FindApi(assemblyPath, pattern, kind, maxMatches);
}
