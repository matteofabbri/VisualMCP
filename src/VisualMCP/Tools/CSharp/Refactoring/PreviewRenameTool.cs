using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class PreviewRenameTool
{
    [McpServerTool, Description(
        "Preview exactly what renaming a symbol would change across the solution WITHOUT writing anything — the safe first step before apply_rename. " +
        "Use this INSTEAD OF guessing the blast radius from grep: Roslyn lists the real references that would change. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> PreviewRename(
        IPreviewRenameService svc,
        [Description("Current symbol name (class, method, property, field, etc.)")] string currentName,
        [Description("New name to rename to")] string newName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null)
        => svc.PreviewRename(currentName, newName, containingType);
}
