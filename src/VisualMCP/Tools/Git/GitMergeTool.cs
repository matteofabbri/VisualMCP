using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitMergeTool
{
    [McpServerTool(Name = "git_merge"), Description(
        "Merge a branch into the current one. strategy: default, no-ff (always a merge commit), ff-only (refuse " +
        "rather than create a commit) or squash (stage the changes without committing). On conflict it returns the " +
        "conflicted files; resolve them and commit, or call again with abort=true to undo the merge. " +
        "Use this INSTEAD OF shell 'git merge'.")]
    public static Task<object> GitMerge(
        IGitService git,
        [Description("Branch or revision to merge into the current branch.")] string branch,
        [Description("default | no-ff | ff-only | squash")] string strategy = "default",
        [Description("Commit message for the merge commit.")] string? message = null,
        [Description("Abort an in-progress merge instead of starting one.")] bool abort = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.MergeAsync(branch, strategy, message, abort, repoPath);
}
