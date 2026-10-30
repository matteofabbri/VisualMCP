using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class TypeToPartialTool
{
    [McpServerTool(Name = "type_to_partial"), Description(
        "Mark a type `partial` in EVERY file that declares it (Rider's 'Make partial'), which is what you need before " +
        "splitting a class across files. Adding it to only one declaration would not compile, so the tool walks all " +
        "declarations and reports how many it touched; enums and delegates cannot be partial and are refused. " +
        "dryRun defaults to true and returns a line diff per file.")]
    public static Task<object> TypeToPartial(
        ITypeToPartialService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Type name (class, struct, interface or record).")] string typeName,
        [Description("Optional containing type name, when a nested type has the same name.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.TypeToPartial(filePath, typeName, containingType, dryRun);
}
