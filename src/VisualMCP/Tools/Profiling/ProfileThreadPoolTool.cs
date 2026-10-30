using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Verdicts;
using Snitch.Workspace;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileThreadPoolTool
{
    [McpServerTool(Name = "profile_threadpool"), Description(
        "Answer 'is the thread pool starved?' with a verdict — starved, at-risk, burst, healthy or inconclusive — not just a " +
        "queue number. A full queue is not starvation and an empty one does not mean health: the service requires both " +
        "halves of the signature (work waiting AND threads that cannot take it), which is why it collects the thread-pool " +
        "counters, a 'runtime' trace (the sample profiler supplies the blocked intervals) and the live stacks together, then " +
        "names the blocking site with file:line. 'burst' means a spike that drained (no action needed), 'at-risk' means the " +
        "wait is real but has not produced the full signature yet, 'inconclusive' means no queue signal was available. Use " +
        "it when async work stalls, when a service answers slowly under load, or when a stack dump shows many threads " +
        "parked in Task.Wait/Task.Result. Writes the trace under <solution>/.visualMCP/profiling.")]
    public static async Task<object> ThreadPool(
        IThreadPoolStarvationService pool,
        IProfiledProcessService processes,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Target process id. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("How long to sample counters and trace, in seconds (bounded by the server's max).")] int durationSeconds = 10,
        [Description("How many blocking sites to return (1-100).")] int top = 15,
        [Description("Resolve blocking sites to file:line using portable PDBs (default true).")] bool resolveSource = true,
        [Description("Optional root used to tell user code from framework code in the I/O analysis.")] string? userCodeRoot = null)
    {
        try
        {
            var (target, error) = processes.Resolve(processId, processName);
            if (target is null) return new { error };

            var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
            return await pool.AnalyzeProcessAsync(
                target.ProcessId, target.Name, durationSeconds, directory, top, resolveSource, userCodeRoot);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
