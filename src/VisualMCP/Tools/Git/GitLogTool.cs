using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitLogTool
{
    [McpServerTool(Name = "git_log"), Description(
        "Show recent git commits of the repository containing the loaded solution: hash, author, ISO date " +
        "and subject, most recent first. Optionally restrict to a file/directory path. Use this INSTEAD OF " +
        "a shell 'git log' for a structured result. Read-only.")]
    public static Task<object> GitLog(
        [Description("How many commits to return (default: 20, max: 200).")] int maxCount = 20,
        [Description("Optional: only commits that touched this file or directory path.")] string? path = null)
        => GitImpl.LogAsync(maxCount, path);
}
