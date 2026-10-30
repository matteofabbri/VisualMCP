using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class DisassembleTool
{
    [McpServerTool(Name = "disassemble"), Description(
        "The IL view of a compiled .NET assembly: each method body decoded into instructions, with tokens " +
        "resolved against the metadata ('call System.String::Concat', 'ldstr \"…\"'). Filter by typeName and/or " +
        "methodName, or read the first maxMethods. Read-only — nothing is executed and no Roslyn is involved. " +
        "This is the missing 'toggle C# ↔ IL' of the decompiled workspace: disassemble for the IL, " +
        "decompile_type / assembly_workspace for the C#.")]
    public static object Disassemble(
        IDisassemblyService disassembly,
        [Description("Path to the .dll/.exe.")] string assemblyPath,
        [Description("Only this type (full or simple name).")] string? typeName = null,
        [Description("Only this method name. Needs typeName when the name is common.")] string? methodName = null,
        [Description("Maximum method bodies to return (default 50, max 500).")] int maxMethods = 50)
        => disassembly.Disassemble(assemblyPath, typeName, methodName, maxMethods);
}
