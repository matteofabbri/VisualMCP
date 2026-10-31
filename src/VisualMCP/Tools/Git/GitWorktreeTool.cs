using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitWorktreeTool
{
    [McpServerTool(Name = "git_worktree"), Description(
        "Manage linked working trees: list them (structured: path, branch, HEAD, detached), add one " +
        "(optionally on a new branch with newBranch=true), remove one (force=true discards its uncommitted " +
        "changes) or prune the stale metadata. Use this INSTEAD OF shell 'git worktree ...'.")]
    public static Task<object> GitWorktree(
        IGitService git,
        [Description("list (default) | add | remove | prune.")] string action = "list",
        [Description("For add: the directory for the new worktree. For remove: the worktree path to remove.")] string? path = null,
        [Description("For add: a branch to check out in the new worktree (created first when newBranch=true).")] string? branch = null,
        [Description("For add: create the named branch instead of checking out an existing one (default false).")] bool newBranch = false,
        [Description("For remove: remove the worktree even with uncommitted changes (default false).")] bool force = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.WorktreeAsync(action, path, branch, newBranch, force, repoPath);
}
