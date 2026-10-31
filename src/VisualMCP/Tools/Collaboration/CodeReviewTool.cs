using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Collaboration;

namespace VisualMCP.Tools.Collaboration;

[McpServerToolType]
public static class CodeReviewTool
{
    [McpServerTool(Name = "code_review"), Description(
        "The review workflow for a change: give it the base it is compared with (default HEAD, i.e. the working " +
        "tree) and it returns the brief a reviewer starts from — every changed file with its status, additions and " +
        "deletions and its kind (source/test/ui/docs/config), a checklist of the signals that matter first (a " +
        "deleted test, source changed with no test changed, a 400+ line file, newly added TODO/FIXME markers with " +
        "their new-file line), the worst methods (cyclomatic/cognitive) of each changed C# file, and — with " +
        "includeInspections=true — the inspect_code findings that land in the changed files. It composes git, the " +
        "complexity engine and the inspection engine; it runs no tests (run_affected_tests does) and does not post " +
        "anything to a remote (gh_pr action=review does). Use this INSTEAD OF assembling the same picture by hand " +
        "before a review.")]
    public static Task<object> CodeReview(
        IChangeReviewService review,
        [Description("The revision or range the change is compared with; default HEAD (the working tree).")] string baseRef = "HEAD",
        [Description("Only this file or folder of the repository.")] string? path = null,
        [Description("How many changed files the response lists (default 100; the summary always counts them all).")] int maxFiles = 100,
        [Description("Also run the inspection packs and return the findings in the changed files (slower: a whole-solution inspection).")] bool includeInspections = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null,
        [Description("Budget for the inspection run, in seconds (default 120).")] int timeoutSeconds = 120)
        => review.ReviewAsync(baseRef, path, maxFiles, includeInspections, repoPath, timeoutSeconds);
}
