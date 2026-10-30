using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class PushMembersDownTool
{
    [McpServerTool(Name = "push_members_down"), Description(
        "Move one method, property or field from a base type into its derived type (Rider's 'Push members down'), for " +
        "the case that is a pure relocation: the base has exactly ONE derived type in source and the member is used only " +
        "inside it. With several derived types, or a use through the base type, moving the member would change what those " +
        "callers can do — so the tool refuses and says which use blocks it. GATED on the compiler. dryRun defaults to " +
        "true.")]
    public static Task<object> PushMembersDown(
        IPushMembersDownService svc,
        [Description("File that declares the base type (absolute or relative).")] string filePath,
        [Description("Member to move (method, property or field).")] string memberName,
        [Description("Optional base type name, when it is not unique in the file.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.PushMembersDown(filePath, memberName, containingType, dryRun);
}
