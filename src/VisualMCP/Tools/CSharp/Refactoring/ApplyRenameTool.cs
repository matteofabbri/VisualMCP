using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ApplyRenameTool
{
    [McpServerTool, Description(
        "When you need to rename a symbol everywhere, use this INSTEAD OF find/replace or manual edits. " +
        "Roslyn updates every real reference across the solution (respecting scope, overloads and overrides) and writes to disk, " +
        "without touching same-named-but-unrelated identifiers that text replace would corrupt. Run preview_rename first to see the impact. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> ApplyRename(
        IApplyRenameService svc,
        [Description("Current symbol name (class, method, property, field, etc.)")] string currentName,
        [Description("New name to rename to")] string newName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null,
        [Description("Also rename overloads with the same name (default: false)")] bool renameOverloads = false,
        [Description("Also rename occurrences inside string literals (default: false)")] bool renameInStrings = false,
        [Description("Also rename occurrences inside comments (default: false)")] bool renameInComments = false)
        => svc.ApplyRename(currentName, newName, containingType, renameOverloads, renameInStrings, renameInComments);
}