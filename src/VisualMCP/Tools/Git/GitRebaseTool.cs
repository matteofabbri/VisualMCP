using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitRebaseTool
{
    [McpServerTool(Name = "git_rebase"), Description(
        "Rebase the current branch: action=start onto a revision, then continue/skip/abort when it stops on a " +
        "conflict (the response lists the conflicted files). action=plan is the interactive rebase WITHOUT an " +
        "editor: pass onto (the commit BEFORE the first step) and plan, a JSON array of " +
        "[{\"commit\":\"hash\",\"action\":\"pick|reword|squash|fixup|drop\",\"message\":\"…\"}] and the commits are " +
        "reordered/reworded/folded/dropped exactly as listed; reword and squash take their new message from the " +
        "step (squash without one keeps the previous commit's message), and no editor is ever opened. " +
        "On a conflict it returns the conflicted files; git_conflicts resolves them. Use this INSTEAD OF shell " +
        "'git rebase' / 'git rebase -i'.")]
    public static Task<object> GitRebase(
        IGitService git,
        [Description("start | continue | abort | skip | plan")] string action = "start",
        [Description("For start and plan: the revision to rebase onto / the commit before the first step (e.g. 'origin/main', 'HEAD~4').")] string? onto = null,
        [Description("For action=plan: JSON array of steps, e.g. [{\"commit\":\"abc1234\",\"action\":\"pick\"},{\"commit\":\"def5678\",\"action\":\"reword\",\"message\":\"new subject\"}].")] string? plan = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.RebaseAsync(onto, action, plan ?? string.Empty, repoPath);
}
