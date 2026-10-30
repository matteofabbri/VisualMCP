using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ChangeTypeTool
{
    [McpServerTool(Name = "change_type"), Description(
        "Replace a declaration's type (Rider's 'Change type'), GATED on the compiler: the project is compiled with the new " +
        "type and the change is written only when it adds no errors — otherwise the resulting errors are reported and " +
        "nothing is touched. This is the safe way to try 'List<T>' → 'IEnumerable<T>' or a base type: the compiler says " +
        "whether the usages support it. Works on fields, properties, locals, parameters and non-void return types; " +
        "locates the declaration by its start line. dryRun defaults to true.")]
    public static Task<object> ChangeType(
        IChangeTypeService svc,
        [Description("File containing the declaration (absolute or relative).")] string filePath,
        [Description("1-based line where the declaration starts.")] int line,
        [Description("The new type, as written in source (e.g. IEnumerable<int>).")] string newType,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.ChangeType(filePath, line, newType, dryRun);
}
