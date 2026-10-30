using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitRunTool
{
    [McpServerTool(Name = "git_run"), Description(
        "Escape hatch: run ANY git command in the repository and get exit code, stdout and stderr. Use it for what " +
        "the structured git_* tools do not cover (reflog, worktree, bisect, submodule, filter-repo, ...). " +
        "The arguments are passed verbatim, so you spell the command out yourself. Destructive commands are not " +
        "confirmed for you here — prefer the structured tools (git_reset, git_squash, ...) when they fit.")]
    public static Task<object> GitRun(
        IGitService git,
        [Description("Git arguments verbatim, e.g. 'reflog -n 20' or 'worktree list'.")] string args,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null,
        [Description("Timeout in seconds (default 120, max 900).")] int timeoutSeconds = 120)
        => git.RunAsync(args, repoPath, timeoutSeconds);
}
