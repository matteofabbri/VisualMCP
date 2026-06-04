using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitDiffTool
{
    [McpServerTool(Name = "git_diff"), Description(
        "Show a git diff of the repository containing the loaded solution. By default shows unstaged " +
        "working-tree changes; set target='staged' for staged changes, or a ref/range (e.g. 'HEAD~1', " +
        "'main..HEAD') to diff against that. Optionally limit to a path, and use summaryOnly for a " +
        "per-file change-stat instead of the full patch. Output is truncated. Use this INSTEAD OF a shell " +
        "'git diff'. Read-only.")]
    public static Task<object> GitDiff(
        [Description("What to diff: 'working' (unstaged, default), 'staged', or a git ref/range like 'HEAD~1' or 'main..HEAD'.")] string target = "working",
        [Description("Optional: limit the diff to this file or directory path.")] string? path = null,
        [Description("Return a per-file summary (--stat) instead of the full patch (default: false).")] bool summaryOnly = false)
        => GitImpl.DiffAsync(target, path, summaryOnly);
}
