using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitAmendTool
{
    [McpServerTool(Name = "git_amend"), Description(
        "Reword the last commit: replaces its message and keeps its tree. Nothing else is changed — this is the " +
        "non-interactive way to fix a typo in the message. Use this INSTEAD OF shell 'git commit --amend -m'.")]
    public static Task<object> GitAmend(
        IGitService git,
        [Description("The new commit message (multi-line allowed).")] string message,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.AmendAsync(message, repoPath);
}
