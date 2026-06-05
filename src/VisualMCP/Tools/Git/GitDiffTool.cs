using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitDiffTool
{
    [McpServerTool(Name = "git_diff"), Description(
        "Show a git diff of a repository. By default shows unstaged working-tree changes; set target='staged' " +
        "for staged changes, or a ref/range (e.g. 'HEAD~1', 'main..HEAD'). Optionally limit to a path, and use " +
        "summaryOnly for a per-file change-stat. Targets the loaded solution's repo unless repoPath is given. " +
        "Output is truncated. Read-only.")]
    public static Task<object> GitDiff(
        [Description("What to diff: 'working' (unstaged, default), 'staged', or a git ref/range like 'HEAD~1' or 'main..HEAD'.")] string target = "working",
        [Description("Optional: limit the diff to this file or directory path.")] string? path = null,
        [Description("Return a per-file summary (--stat) instead of the full patch (default: false).")] bool summaryOnly = false,
        [Description("Optional: path to the git repository. Defaults to the loaded solution's repo.")] string? repoPath = null)
        => GitImpl.DiffAsync(target, path, summaryOnly, repoPath);
}
