using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class GitChurnHotspotsTool
{
    [McpServerTool(Name = "git_churn_hotspots"), Description(
        "Where does a refactoring pay first? Joins two signals that each point at the wrong file alone: git " +
        "history (how often a file changes — 'git log --numstat' over a bounded window) and Roslyn complexity " +
        "(how hard its worst method is to read). A file that changes every week AND carries cyclomatic 40 is a " +
        "hotspot; a big file nobody touches is not, and a trivial file everyone edits is not either. Returns the " +
        "ranked files with commits, changed lines, worst method with file:line, and the score (commits × worst " +
        "cyclomatic) with its share of the total. Read-only: nothing is written and no runtime measurement is " +
        "implied. Narrow it with since (default '1 year ago'), a path, a project, or minCommits (default 3).")]
    public static Task<object> GitChurnHotspots(
        IGitChurnHotspotsService svc,
        [Description("Optional: the repository to read (default: the loaded solution's repository).")] string? repoPath = null,
        [Description("Optional: only complexity from this project (the churn window is not filtered).")] string? projectName = null,
        [Description("How far back to read git history (any git date, e.g. '6 months ago', '2025-01-01'; default '1 year ago').")] string? since = null,
        [Description("Optional: only files under this path (repository-relative).")] string? path = null,
        [Description("How many hotspots to return (default 25, max 500).")] int top = 25,
        [Description("A file must have changed at least this many times in the window (default 3).")] int minCommits = 3,
        [Description("Maximum commits to read (default 2000) — the bound that keeps the call predictable.")] int maxCommits = 2000)
        => svc.AnalyzeAsync(repoPath, projectName, since, path, top, minCommits, maxCommits);
}
