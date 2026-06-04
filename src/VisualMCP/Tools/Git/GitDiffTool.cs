using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Tools.Execution;

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
    public static async Task<object> GitDiff(
        [Description("What to diff: 'working' (unstaged, default), 'staged', or a git ref/range like 'HEAD~1' or 'main..HEAD'.")] string target = "working",
        [Description("Optional: limit the diff to this file or directory path.")] string? path = null,
        [Description("Return a per-file summary (--stat) instead of the full patch (default: false).")] bool summaryOnly = false)
    {
        var (repoDir, error) = await GitCli.ResolveRepoAsync();
        if (error is not null) return error;

        var t = (target ?? "working").Trim();
        var args = "diff";
        if (string.Equals(t, "staged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t, "cached", StringComparison.OrdinalIgnoreCase))
            args += " --cached";
        else if (!string.Equals(t, "working", StringComparison.OrdinalIgnoreCase) && t.Length > 0)
            args += $" {t}";

        if (summaryOnly) args += " --stat";
        if (!string.IsNullOrWhiteSpace(path)) args += $" -- \"{path}\"";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 45);
        if (timedOut) return new { error = "git diff timed out." };
        if (exitCode != 0) return new { error = $"git diff failed: {stderr.Trim()}", command = $"git {args}" };

        var diff = stdout;
        return new
        {
            repoDir,
            target = t,
            pathFilter = path,
            summaryOnly,
            command = $"git {args}",
            empty = string.IsNullOrWhiteSpace(diff),
            diff = ProcessRunner.Truncate(diff, 32_000),
        };
    }
}
