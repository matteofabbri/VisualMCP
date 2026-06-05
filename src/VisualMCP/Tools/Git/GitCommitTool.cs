using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitCommitTool
{
    [McpServerTool(Name = "git_commit"), Description(
        "Create a git commit from the staged changes in a repository, using the given message (multi-line " +
        "allowed, no length limit — it is written via a temp file). Set stageAll=true to stage every change " +
        "first. Targets the loaded solution's repo unless repoPath is given. Returns the new commit hash. " +
        "Does NOT push and does NOT amend. Fails if nothing is staged unless allowEmpty=true.")]
    public static Task<object> GitCommit(
        [Description("The commit message. May span multiple lines; no length limit.")] string message,
        [Description("Stage all changes (git add -A) before committing. Default: false.")] bool stageAll = false,
        [Description("Allow creating a commit with no changes. Default: false.")] bool allowEmpty = false,
        [Description("Optional: path to the git repository. Defaults to the loaded solution's repo.")] string? repoPath = null)
        => GitImpl.CommitAsync(message, stageAll, allowEmpty, repoPath);
}
