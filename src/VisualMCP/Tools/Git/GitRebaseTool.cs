using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitRebaseTool
{
    [McpServerTool(Name = "git_rebase"), Description(
        "Rebase the current branch: action=start onto a revision, then continue/skip/abort when it stops on a " +
        "conflict (the response lists the conflicted files). Use this INSTEAD OF shell 'git rebase'.")]
    public static Task<object> GitRebase(
        IGitService git,
        [Description("start | continue | abort | skip")] string action = "start",
        [Description("For action=start: the revision to rebase onto (e.g. 'origin/main').")] string? onto = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.RebaseAsync(onto, action, repoPath);
}
