using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveStaticMembersTool
{
    [McpServerTool(Name = "move_static_members"), Description(
        "Rider's 'Move static members': move static methods, properties and fields to another type and re-point " +
        "every use — both `Source.M(...)` and the bare `M(...)` inside the source type become `Target.M(...)`, and " +
        "the target namespace is imported where it was not visible. Refuses instance members (use " +
        "move_instance_method) and name collisions on the target. Compiler-gated. dryRun defaults to true.")]
    public static Task<object> MoveStaticMembers(
        IMoveStaticMembersService svc,
        [Description("File that declares the source type (absolute or relative).")] string filePath,
        [Description("Type the members currently live in.")] string sourceTypeName,
        [Description("Static members to move (methods, properties, single-variable fields).")] string[] memberNames,
        [Description("Type that receives them: a simple name, or a fully qualified one when ambiguous.")] string targetTypeName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MoveStaticMembers(filePath, sourceTypeName, memberNames, targetTypeName, dryRun);
}
