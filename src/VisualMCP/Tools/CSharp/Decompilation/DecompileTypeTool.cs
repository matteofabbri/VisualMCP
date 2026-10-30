using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class DecompileTypeTool
{
    [McpServerTool(Name = "decompile_type"), Description(
        "Decompile a single type from a compiled .NET assembly (.dll) back to C# source, using the " +
        "ILSpy engine (ICSharpCode.Decompiler). Use this to read the actual implementation of a third-party " +
        "or reference-only type — INSTEAD OF guessing from signatures. Pair with inspect_assembly (to find " +
        "type names) and locate_nuget_package (to find the DLL). For SEVERAL types of one assembly use " +
        "decompile_types; to KEEP the decompilation as a queryable metacode tree, use metacoding action=decompile. " +
        "The C# is compacted by default: ILSpy's " +
        "indentation, line breaks and comments are gone and the same tokens come back in about half the " +
        "characters (pass compact=false for the readable form). Read-only; metadata + IL only.")]
    public static object DecompileType(
        IDecompileService decompile,
        [Description("Path to the .dll containing the type.")] string assemblyPath,
        [Description("Fully-qualified type name, e.g. 'System.Text.Json.JsonSerializer'.")] string typeFullName,
        [Description("Maximum characters of C# to return (default: 60000).")] int maxChars = 60000,
        [Description("Strip indentation, line breaks and comments (default: true). Pass false for ILSpy's readable formatting.")] bool compact = true)
        => decompile.DecompileType(assemblyPath, typeFullName, maxChars, compact);
}