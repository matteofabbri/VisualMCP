using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis.QualityTrend;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class QualityTrendTool
{
    [McpServerTool(Name = "get_quality_trend"), Description(
        "Did this branch make the code better or worse? Compares two revisions — or `to=worktree` for what is " +
        "uncommitted — measuring both sides with ONE syntactic rule set: average cyclomatic complexity per method, " +
        "methods over 10 and over 20, long methods (over 40 lines) and TODO markers, with size (files, lines, types, " +
        "methods) as context that never changes the verdict. Returns improved/regressed/unchanged, every signal with " +
        "its direction and percent delta, the files whose complexity moved most in each direction, and the added and " +
        "removed files. Nothing is compiled, so a revision that no longer builds is still comparable. " +
        "Complements find_breaking_changes (public API) and verify_change (test outcomes): this is the code health " +
        "between two points in history.")]
    public static Task<object> GetQualityTrend(
        IQualityTrendService svc,
        [Description("Baseline revision: a branch, tag, 'HEAD~5' or a commit hash (default: HEAD~1)")] string? from = null,
        [Description("Revision to compare against, or 'worktree' for the uncommitted tree (default: HEAD)")] string? to = null,
        [Description("Repository path; defaults to the loaded solution's repository")] string? repoPath = null,
        [Description("Only this folder or .cs file, relative to the repository root")] string? path = null,
        [Description("How many files to return per direction (default 10)")] int top = 10)
        => svc.MeasureAsync(from, to, repoPath, path, top);
}
