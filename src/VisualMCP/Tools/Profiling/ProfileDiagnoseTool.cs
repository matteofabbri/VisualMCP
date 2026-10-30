using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Runtime;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileDiagnoseTool
{
    [McpServerTool(Name = "profile_diagnose"), Description(
        "One-call diagnosis of a live .NET process: it collects EventCounters, an EventPipe trace, the managed stacks " +
        "and (optionally) a heap snapshot, runs every rule set, and CORRELATES the signals into prioritized findings — " +
        "e.g. 'cpu-usage peaked at 96% while method X owned 71% of CPU samples', or 'thread-pool queue grew to 4,000 " +
        "with 9 threads blocked in synchronous waits'. Writes one Markdown report under <solution>/.visualMCP/profiling " +
        "and returns the structured report. Use it as the first deep call when you know the process is unhealthy but " +
        "not why; use the specific profile_* tools to drill into one signal. The call takes durationSeconds plus the " +
        "analysis time (the trace is converted for CPU/allocation stacks).")]
    public static async Task<object> Diagnose(
        IProfileDiagnosisService diagnosis,
        IProfiledProcessService processes,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Target process id. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("How long to trace, in seconds (counters sample the first min(5, duration) seconds).")] int durationSeconds = 10,
        [Description("Take and analyze a heap snapshot as part of the diagnosis.")] bool includeHeap = true,
        [Description("Capture the managed stacks of every thread as part of the diagnosis.")] bool includeStacks = true,
        [Description("How many hotspots/types to return per section (1-200).")] int top = 15)
    {
        try
        {
            var (target, error) = processes.Resolve(processId, processName);
            if (target is null)
                return new { error };

            var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
            var report = await diagnosis.DiagnoseAsync(
                target.ProcessId, target.Name, durationSeconds, includeHeap, includeStacks, top, directory);

            return report;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
