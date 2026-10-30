using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Verdicts;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileReviewChangeTool
{
    [McpServerTool(Name = "profile_review_change"), Description(
        "Answer 'is this change a regression?' with a verdict — regression, improvement, unchanged or inconclusive — where " +
        "profile_compare only gives you the delta table. It restates both runs in units that survive different collection " +
        "windows (CPU ms per second of trace, allocation MB per second, exceptions per second, GC pause ms per second), " +
        "applies a significance floor per metric so a 2% wobble reads as 'unchanged' instead of as a regression, and REFUSES " +
        "to judge two windows shorter than 500 ms ('inconclusive', with the reason). Under 100 CPU samples it drops the CPU " +
        "metric from the comparison rather than the whole verdict, so another metric can still win — and the answer says " +
        "so in its notes. Works on two .nettrace files (kind='trace'), two heap snapshot JSONs (kind='heap', profile_heap " +
        "writeJson=true) or kind='auto' by extension, and names the method or type that drove the verdict. Use it after " +
        "measure → change → measure, before claiming a fix or a regression; use profile_compare when you want every delta.")]
    public static async Task<object> ReviewChange(
        IChangeReviewService review,
        [Description("Left/baseline file (.nettrace, or heap .json).")] string baselinePath,
        [Description("Right/current file (.nettrace, or heap .json).")] string currentPath,
        [Description("'auto' (default, by extension), 'trace' or 'heap'.")] string kind = "auto",
        [Description("How many deltas to return per section (1-200).")] int top = 25)
    {
        try
        {
            return await review.ReviewAsync(baselinePath, currentPath, kind, top);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
