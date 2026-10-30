using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Verdicts;
using Snitch.Workspace;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileSlowdownTool
{
    [McpServerTool(Name = "profile_slowdown"), Description(
        "Answer 'why is this process slow?' with ONE verdict instead of a table of numbers: every candidate cause — " +
        "cpu, gc, io-wait, sync-over-async, lock-wait, blocked-native — is scored in the same unit (the share of the " +
        "measured window it can explain), the winner becomes a single sentence, and the runners-up stay visible with the " +
        "reason they lost. Live mode samples counters, collects a 'diagnose' trace and the stacks, then answers; pass " +
        "tracePath to answer from a trace you already have (counters and stacks are then absent, and the answer says so at " +
        "lower confidence). When nothing reaches 10% of the window the verdict is 'not-slow' — that is an answer, not a " +
        "failure. Use it as the first call when someone says 'it is slow' but no single signal has been named yet; use " +
        "profile_collect/profile_analyze/profile_io to drill into the cause it names. Writes the trace under " +
        "<solution>/.visualMCP/profiling.")]
    public static async Task<object> Slowdown(
        ISlowdownService slowdown,
        IProfiledProcessService processes,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("An existing .nettrace to answer from. Omit to measure a live process.")] string? tracePath = null,
        [Description("Live mode: target process id. Omit if processName is given.")] int? processId = null,
        [Description("Live mode: target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("Live mode: how long to sample counters and trace, in seconds (bounded by the server's max).")] int durationSeconds = 10,
        [Description("Live mode: also capture the managed stacks of every thread — this is what names the holder of a contended lock.")]
        bool includeStacks = true,
        [Description("How many findings/hotspots to return per section (1-200).")] int top = 15,
        [Description("Resolve method names to file:line using portable PDBs (default true).")] bool resolveSource = true,
        [Description("Optional root used to tell user code from framework code in the I/O analysis.")] string? userCodeRoot = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tracePath))
            {
                var fromTrace = await slowdown.AnalyzeTraceAsync(
                    tracePath, counters: null, stacks: null,
                    top: top, resolveSource: resolveSource, userCodeRoot: userCodeRoot);
                return fromTrace;
            }

            var (target, error) = processes.Resolve(processId, processName);
            if (target is null) return new { error };

            var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
            return await slowdown.AnalyzeProcessAsync(
                target.ProcessId, target.Name, durationSeconds, includeStacks, directory, top, resolveSource, userCodeRoot);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
