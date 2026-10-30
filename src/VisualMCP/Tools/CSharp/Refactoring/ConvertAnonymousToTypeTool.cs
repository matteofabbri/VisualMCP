using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ConvertAnonymousToTypeTool
{
    [McpServerTool(Name = "convert_anonymous_to_type"), Description(
        "Rider's 'Convert anonymous type to declared type': turn one `new { ... }` into a real class in its own " +
        "file — constructor plus read-only properties, with the member types the compiler inferred — and rewrite " +
        "the creation to `new TypeName(values...)`. The class is internal sealed and lands in the same namespace. " +
        "Compiler-gated. dryRun defaults to true.")]
    public static Task<object> ConvertAnonymousToType(
        IConvertAnonymousToTypeService svc,
        [Description("File that contains the anonymous creation (absolute or relative).")] string filePath,
        [Description("1-based line of the `new { ... }` expression.")] int line,
        [Description("Name of the generated class; defaults to 'AnonymousType'.")] string? typeName = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.ConvertAnonymousToType(filePath, line, typeName, dryRun);
}
