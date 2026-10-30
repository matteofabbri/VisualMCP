using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitSquashTool
{
    [McpServerTool(Name = "git_squash"), Description(
        "Squash the last N commits into one with a new message (soft reset + commit). Refused when the working tree " +
        "is not clean, because its changes would be folded into the squashed commit. Returns the new commit. " +
        "Use this INSTEAD OF an interactive rebase when the commits to merge are the most recent ones.")]
    public static Task<object> GitSquash(
        IGitService git,
        [Description("How many trailing commits to fold together (2 or more).")] int count,
        [Description("Message for the resulting commit (multi-line allowed).")] string message,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.SquashAsync(count, message, repoPath);
}
