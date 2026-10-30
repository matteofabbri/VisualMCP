using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveToFolderTool
{
    [McpServerTool(Name = "move_to_folder"), Description(
        "Rider's 'Move to folder': put a file in the folder that matches its namespace (relative to the project " +
        "directory, with the root namespace mapped to the project folder), or in an explicit folder. The file " +
        "content is unchanged and the model is updated, so the next call sees the new path. Refuses when the " +
        "project lists the file explicitly with <Compile Include>. dryRun defaults to true.")]
    public static Task<object> MoveToFolder(
        IMoveToFolderService svc,
        [Description("File to move (absolute or relative).")] string filePath,
        [Description("Optional target folder: absolute, or relative to the project directory. Omit to derive it from the namespace.")] string? folder = null,
        [Description("true (default) previews only; pass false to move the file.")] bool dryRun = true)
        => svc.MoveToFolder(filePath, folder, dryRun);
}
