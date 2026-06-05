using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class DecompileAssemblyTool
{
    [McpServerTool(Name = "decompile_assembly"), Description(
        "Decompile a whole .NET assembly (.dll) to C# source using the ILSpy engine (ICSharpCode.Decompiler). " +
        "Output can be very large, so pass outputPath to write the full C# to a file; otherwise a truncated " +
        "preview is returned. For a single type prefer decompile_type. Read-only; metadata + IL only.")]
    public static object DecompileAssembly(
        [Description("Path to the .dll to decompile.")] string assemblyPath,
        [Description("Optional: file path to write the full decompiled C# to. If omitted, a truncated preview is returned.")] string? outputPath = null,
        [Description("Maximum characters of C# to return when no outputPath is given (default: 60000).")] int maxChars = 60000)
        => DecompileImpl.DecompileAssembly(assemblyPath, outputPath, maxChars);
}
