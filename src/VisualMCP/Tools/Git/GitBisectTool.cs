using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitBisectTool
{
    [McpServerTool(Name = "git_bisect"), Description(
        "Binary-search the history for the commit that introduced a bug: start the session (with optional " +
        "bad/good boundary revisions), mark the checked-out commit as good or bad (optionally by revision), " +
        "skip an untestable commit, read the bisect log, view the remaining range, or reset to leave the " +
        "bisecting state. The answer reports when the bisection is finished ('first bad commit'). Use this " +
        "INSTEAD OF shell 'git bisect ...'.")]
    public static Task<object> GitBisect(
        IGitService git,
        [Description("start | good | bad | skip | log | view | reset (default log).")] string action = "log",
        [Description("For start: the known-bad boundary revision. For bad: the revision to mark bad (default HEAD).")] string? bad = null,
        [Description("For start: the known-good boundary revision. For good: the revision to mark good (default HEAD).")] string? good = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.BisectAsync(action, bad, good, repoPath);
}
