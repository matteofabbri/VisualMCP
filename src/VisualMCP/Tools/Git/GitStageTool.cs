using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitStageTool
{
    [McpServerTool(Name = "git_stage"), Description(
        "Stage changes for commit (git add) in the loaded solution's repository. Pass specific paths, or " +
        "set all=true to stage everything. Follow with git_commit. Does NOT push. Returns the staged files.")]
    public static Task<object> GitStage(
        [Description("Paths (files or directories) to stage. Omit and set all=true to stage everything.")] string[]? paths = null,
        [Description("Stage all changes in the repo (git add -A). Default: false.")] bool all = false)
        => GitImpl.StageAsync(paths, all);
}
