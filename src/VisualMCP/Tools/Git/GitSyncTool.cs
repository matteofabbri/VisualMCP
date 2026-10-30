using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitSyncTool
{
    [McpServerTool(Name = "git_sync"), Description(
        "Talk to a remote: fetch (updates remote-tracking branches only), pull (fetch + merge/rebase), or push " +
        "(optionally with tags). force=true rewrites published history and therefore REQUIRES confirm=true; it uses " +
        "--force-with-lease. Use this INSTEAD OF shell 'git fetch|pull|push'.")]
    public static Task<object> GitSync(
        IGitService git,
        [Description("fetch | pull | push")] string action = "fetch",
        [Description("Remote name (e.g. 'origin'). Omit to use the branch's configured remote.")] string? remote = null,
        [Description("Branch to pull/push. Omit to use the current branch.")] string? branch = null,
        [Description("For push: also push tags.")] bool tags = false,
        [Description("For push: force, with lease (requires confirm=true).")] bool force = false,
        [Description("Must be true for force=true.")] bool confirm = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.SyncAsync(action, remote, branch, tags, force, confirm, repoPath);
}
