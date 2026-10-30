using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class RemoveRegionsTool
{
    [McpServerTool, Description(
        "When you want to strip #region/#endregion directives, use this INSTEAD OF hand-editing: it removes them across files while leaving the enclosed code intact " +
        "(CodeMaid 'Remove Regions'). The working-directory solution auto-loads on first use.")]
    public static Task<object> RemoveRegions(
        IRemoveRegionsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Dry run — report which files would change without writing (default: false)")] bool dryRun = false)
        => svc.RemoveRegions(projectName, filePath, dryRun);
}
