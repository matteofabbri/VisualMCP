using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitStatusTool
{
    [McpServerTool(Name = "git_status"), Description(
        "Show the git status of a repository: current branch, upstream tracking with ahead/behind counts, " +
        "and the staged, modified, untracked and conflicted files. Targets the loaded solution's repo unless " +
        "repoPath is given (works on ANY repo, .NET or not). Use this INSTEAD OF a shell 'git status'. Read-only.")]
    public static Task<object> GitStatus(
        [Description("Optional: path to the git repository to operate on. Defaults to the loaded solution's repo, or the server's working directory.")] string? repoPath = null)
        => GitImpl.StatusAsync(repoPath);
}
