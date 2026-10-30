using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveTypeToOuterScopeTool
{
    [McpServerTool(Name = "move_type_to_outer_scope"), Description(
        "Move a nested type to the scope of its container (Rider's 'Move type to outer scope'): the declaration becomes a " +
        "sibling of the outer type, every 'Outer.Inner' reference becomes 'Inner', and a missing using is added when the " +
        "referring file could not see the namespace. GATED on the compiler: if the move would add errors (typically a " +
        "reference that relied on the outer type's nesting) nothing is written and the errors are listed. dryRun " +
        "defaults to true.")]
    public static Task<object> MoveTypeToOuterScope(
        IMoveTypeToOuterScopeService svc,
        [Description("File that contains the nested type (absolute or relative).")] string filePath,
        [Description("Name of the nested type.")] string typeName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MoveTypeToOuterScope(filePath, typeName, dryRun);
}
