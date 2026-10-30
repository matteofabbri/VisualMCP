using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitRevertTool
{
    [McpServerTool(Name = "git_revert"), Description(
        "Create a new commit that undoes the given commits — the safe way to reverse published history, because " +
        "the past is not rewritten. On conflict it returns the conflicted files. Use this INSTEAD OF shell 'git revert'.")]
    public static Task<object> GitRevert(
        IGitService git,
        [Description("Commits to undo, in order, e.g. [\"abc1234\"].")] string[] commits,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.RevertAsync(commits, repoPath);
}
