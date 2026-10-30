using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ReorderMembersTool
{
    [McpServerTool, Description(
        "When you want members ordered to a consistent convention, use this INSTEAD OF rearranging by hand: it reorders all types in a file/project " +
        "(public → protected → private; within each: constants, fields, constructors, properties, events, methods, nested types) and writes to disk, preserving each member's body. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> ReorderMembers(
        IReorderMembersService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Dry run — report which files would change without writing (default: false)")] bool dryRun = false)
        => svc.ReorderMembers(projectName, filePath, dryRun);
}
