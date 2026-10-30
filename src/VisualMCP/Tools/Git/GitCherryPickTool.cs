using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitCherryPickTool
{
    [McpServerTool(Name = "git_cherry_pick"), Description(
        "Copy commits onto the current branch. Pass one or more commits (hashes or refs) to apply them, or " +
        "action=continue|abort|skip when it stopped on a conflict. Returns the conflicted files. " +
        "Use this INSTEAD OF shell 'git cherry-pick'.")]
    public static Task<object> GitCherryPick(
        IGitService git,
        [Description("Commits to copy, in order, e.g. [\"abc1234\", \"def5678\"].")] string[]? commits = null,
        [Description("apply (default) | continue | abort | skip")] string action = "apply",
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.CherryPickAsync(commits ?? [], action, repoPath);
}
