using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitStatusTool
{
    [McpServerTool(Name = "git_status"), Description(
        "Show the git status of the repository containing the loaded solution: current branch, upstream " +
        "tracking with ahead/behind counts, and the staged, modified, untracked and conflicted files. " +
        "Use this INSTEAD OF a shell 'git status' to get a structured, parsed result. Read-only.")]
    public static Task<object> GitStatus() => GitImpl.StatusAsync();
}
