using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class DecompileTypeTool
{
    [McpServerTool(Name = "decompile_type"), Description(
        "Decompile a single type from a compiled .NET assembly (.dll) back to readable C# source, using the " +
        "ILSpy engine (ICSharpCode.Decompiler). Use this to read the actual implementation of a third-party " +
        "or reference-only type — INSTEAD OF guessing from signatures. Pair with inspect_assembly (to find " +
        "type names) and locate_nuget_package (to find the DLL). Read-only; metadata + IL only.")]
    public static object DecompileType(
        [Description("Path to the .dll containing the type.")] string assemblyPath,
        [Description("Fully-qualified type name, e.g. 'System.Text.Json.JsonSerializer'.")] string typeFullName,
        [Description("Maximum characters of C# to return (default: 60000).")] int maxChars = 60000)
        => DecompileImpl.DecompileType(assemblyPath, typeFullName, maxChars);
}
