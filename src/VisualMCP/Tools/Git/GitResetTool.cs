using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitResetTool
{
    [McpServerTool(Name = "git_reset"), Description(
        "Move the branch to another commit. mode=soft keeps the changes staged; mixed (default) leaves them in the " +
        "working tree unstaged; hard discards them and REQUIRES confirm=true because uncommitted work is lost. " +
        "Use this INSTEAD OF shell 'git reset'.")]
    public static Task<object> GitReset(
        IGitService git,
        [Description("Revision to reset to, e.g. 'HEAD~2' or a hash.")] string target,
        [Description("soft | mixed | hard")] string mode = "mixed",
        [Description("Must be true for mode=hard.")] bool confirm = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.ResetAsync(target, mode, confirm, repoPath);
}
