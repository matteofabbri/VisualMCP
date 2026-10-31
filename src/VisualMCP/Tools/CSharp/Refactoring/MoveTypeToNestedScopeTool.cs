using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveTypeToNestedScopeTool
{
    [McpServerTool(Name = "move_type_to_nested_scope"), Description(
        "Move a namespace-level type INTO another type (Rider's 'Move type into another type'): the declaration becomes " +
        "a nested type of the target, and every reference outside the two declarations becomes 'Target.Source' (with a " +
        "global:: namespace qualifier when the file cannot see the target's namespace). The target must be in the same " +
        "project. A source file that held only the moved type is deleted. GATED on the whole solution, because a nested " +
        "type has a different metadata name and accessibility: if a reference would no longer bind, nothing is written " +
        "and the errors are listed. Preview by default. The inverse is move_type_to_outer_scope.")]
    public static Task<object> MoveTypeToNestedScope(
        IMoveTypeToNestedScopeService svc,
        [Description("File that declares the type to move (absolute or relative).")] string filePath,
        [Description("Name of the namespace-level type to move.")] string typeName,
        [Description("Name of the type that will contain it.")] string targetTypeName,
        [Description("Optional: file of the target type, when its name is ambiguous.")] string? targetFilePath = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MoveTypeToNestedScope(filePath, typeName, targetTypeName, targetFilePath, dryRun);
}
