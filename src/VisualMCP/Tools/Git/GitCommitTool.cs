using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitCommitTool
{
    [McpServerTool(Name = "git_commit"), Description(
        "Create a git commit from the staged changes in the loaded solution's repository, using the given " +
        "message (multi-line allowed). Set stageAll=true to stage every change first. Returns the new commit " +
        "hash. Does NOT push and does NOT amend. Fails if nothing is staged unless allowEmpty=true.")]
    public static Task<object> GitCommit(
        [Description("The commit message. May span multiple lines.")] string message,
        [Description("Stage all changes (git add -A) before committing. Default: false.")] bool stageAll = false,
        [Description("Allow creating a commit with no changes. Default: false.")] bool allowEmpty = false)
        => GitImpl.CommitAsync(message, stageAll, allowEmpty);
}
