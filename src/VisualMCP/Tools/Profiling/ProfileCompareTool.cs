using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileCompareTool
{
    [McpServerTool(Name = "profile_compare"), Description(
        "Compare two measurements and report the deltas with regression findings — the measure → change → measure " +
        "loop. kind='trace' compares two .nettrace files: total CPU, per-method CPU ms (the method that regressed, " +
        "with its source line), allocation bytes per type, GC generations and pause, exceptions, contention and " +
        "thread-pool backlog. kind='heap' compares two heap snapshots saved as JSON (profile_heap writeJson=true): " +
        "total/LoH/finalizable and per-type byte and instance deltas. kind='auto' picks by file extension. Findings " +
        "fire on a CPU regression (≥100 ms and ≥20%), an allocation regression (≥10 MB and ≥25%), more gen-2 " +
        "collections, more exceptions, or a heap type that grew.")]
    public static async Task<object> Compare(
        IProfilingComparisonService comparison,
        [Description("Left/baseline file (.nettrace, or heap .json).")] string leftPath,
        [Description("Right/current file (.nettrace, or heap .json).")] string rightPath,
        [Description("'auto' (default), 'trace' or 'heap'.")] string kind = "auto",
        [Description("How many deltas to return per section (1-200).")] int top = 25)
    {
        try
        {
            var result = await comparison.CompareAsync(leftPath, rightPath, kind, top);
            return result;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
