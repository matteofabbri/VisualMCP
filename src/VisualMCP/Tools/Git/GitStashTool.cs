using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitStashTool
{
    [McpServerTool(Name = "git_stash"), Description(
        "Park or restore uncommitted work: push (with an optional message), list, pop/apply a stash (optionally by " +
        "name like 'stash@{1}'), drop, show its diff, or clear them all. Returns the remaining stash entries. " +
        "Use this INSTEAD OF shell 'git stash'.")]
    public static Task<object> GitStash(
        IGitService git,
        [Description("push | list | pop | apply | drop | show | clear")] string action = "push",
        [Description("For push: a message that labels the stash.")] string? message = null,
        [Description("For pop/apply/drop/show: which entry, e.g. 'stash@{1}'. Defaults to the latest.")] string? name = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.StashAsync(action, message, name, repoPath);
}
