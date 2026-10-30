using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class InlineClassTool
{
    [McpServerTool(Name = "inline_class"), Description(
        "Rider's 'Inline class': a helper class that exists only as one field of its owner is inlined into the " +
        "owner — its members move in, `_helper.Member` becomes `Member`, the field and the helper declaration " +
        "disappear. Refuses when the type or the field is used anywhere else (another file, a value context), " +
        "when either has a base type/interfaces/static members, and on name collisions. Compiler-gated. " +
        "dryRun defaults to true.")]
    public static Task<object> InlineClass(
        IInlineClassService svc,
        [Description("File that declares both types (absolute or relative).")] string filePath,
        [Description("Type that owns the field.")] string ownerTypeName,
        [Description("Field (or the single-variable field declaration) that holds the helper instance.")] string fieldName,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.InlineClass(filePath, ownerTypeName, fieldName, dryRun);
}
