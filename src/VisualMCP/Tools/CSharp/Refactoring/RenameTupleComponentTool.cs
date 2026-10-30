using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class RenameTupleComponentTool
{
    [McpServerTool(Name = "rename_tuple_component"), Description(
        "Rename one element of a named tuple (Rider's 'Rename tuple component'): the element name in the tuple type on " +
        "the given line, in every other spelling of the same tuple shape, and in every '.oldName' access whose " +
        "receiver is that tuple. Matching is semantic (element types), so an unrelated '.oldName' on another type is " +
        "left alone. Tuple names are compile-time only, so casts and overloads that used the old name still match. " +
        "dryRun defaults to true.")]
    public static Task<object> RenameTupleComponent(
        IRenameTupleComponentService svc,
        [Description("File that declares the tuple type (absolute or relative).")] string filePath,
        [Description("1-based line where the tuple type starts.")] int line,
        [Description("Current element name, e.g. 'Count'.")] string oldName,
        [Description("New element name.")] string newName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.RenameTupleComponent(filePath, line, oldName, newName, dryRun);
}
