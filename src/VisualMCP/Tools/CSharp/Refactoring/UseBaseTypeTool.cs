using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class UseBaseTypeTool
{
    [McpServerTool(Name = "use_base_type"), Description(
        "Replace a declaration's type with one of its base types or inherited interfaces (Rider's 'Use base type'), GATED " +
        "on the compiler: the base type is derived from the current one, the project is compiled and the change is " +
        "written only if it adds no errors. When several base types are available pass baseType. Locates the declaration " +
        "by its start line, like change_type. dryRun defaults to true.")]
    public static Task<object> UseBaseType(
        IUseBaseTypeService svc,
        [Description("File containing the declaration (absolute or relative).")] string filePath,
        [Description("1-based line where the declaration starts.")] int line,
        [Description("Optional base type or interface name; omit when there is only one.")] string? baseType = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.UseBaseType(filePath, line, baseType, dryRun);
}
