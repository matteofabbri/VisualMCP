using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitLogTool
{
    [McpServerTool(Name = "git_log"), Description(
        "Show recent git commits of a repository: hash, author, ISO date and subject, most recent first. " +
        "Optionally restrict to a file/directory path. Targets the loaded solution's repo unless repoPath is " +
        "given. Use this INSTEAD OF a shell 'git log' for a structured result. Read-only.")]
    public static Task<object> GitLog(
        [Description("How many commits to return (default: 20, max: 200).")] int maxCount = 20,
        [Description("Optional: only commits that touched this file or directory path.")] string? path = null,
        [Description("Optional: path to the git repository. Defaults to the loaded solution's repo.")] string? repoPath = null)
        => GitImpl.LogAsync(maxCount, path, repoPath);
}
