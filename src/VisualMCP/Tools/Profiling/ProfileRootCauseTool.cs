using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileRootCauseTool
{
    [McpServerTool(Name = "profile_root_cause"), Description(
        "Answer 'which CODE creates the problem?' by joining a trace with the solution's METACODE AST. Every measured " +
        "site (CPU self time, sampled allocations, blocked time, thrown exceptions) is matched to the declaration that " +
        "contains it, and the AST says what the line is: the loop chain around it with its nesting depth and header " +
        "(foreach (var o in orders)), a LINQ materialiser executed per iteration (ToList/ToArray/Count), an allocation " +
        "inside a loop, a string concatenation per element, an await or a lock in a loop, a synchronous wait " +
        "(Task.Result/.Wait()). The result is a per-site explanation plus findings that name the construct — not just " +
        "file:line. Use it after profile_hotpaths/profile_hotlines has shown WHERE the cost is, to learn WHY. It reads " +
        "the files on disk through the metacode frontends, so it also works when the persisted index is stale. " +
        "userCodeRoot narrows the answer to the solution; minShare drops sites below a percentage of their metric.")]
    public static async Task<object> RootCause(
        IRootCauseService rootCause,
        [Description("Path to the .nettrace file (collected with profile_collect: cpu, gc, diagnose or runtime).")] string tracePath,
        [Description("How many sites to return across all metrics, worst first (1-100).")] int top = 15,
        [Description("Minimum share, in percent of the site's own metric, for a site to be reported (0-100).")] double minShare = 0.5,
        [Description("Optional solution root: only sites whose source is under it (or is non-framework code) are reported.")] string? userCodeRoot = null,
        [Description("Resolve frames to file:line with the portable PDBs next to the modules (default true; without it there is no AST join).")] bool resolveSource = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await rootCause.AnalyzeAsync(tracePath, top, minShare, userCodeRoot, resolveSource, cancellationToken);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
