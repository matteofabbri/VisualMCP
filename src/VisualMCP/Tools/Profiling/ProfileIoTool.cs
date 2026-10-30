using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileIoTool
{
    [McpServerTool(Name = "profile_io"), Description(
        "Find where threads are blocked in I/O, with duration and call site — not just a snapshot. From a .nettrace " +
        "collected with stack walks (profile='runtime' or 'diagnose') it reads the intervals in which a thread was " +
        "sampled outside managed code and attributes each wait to the managed stack that caused it. Returns Sites " +
        "(category, user call site with file:line, blocking method, blocked ms, share, threads, samples, stack, " +
        "recommendation), Categories (File, Http, Network, Database, Pipe, Console, SyncOverAsync, Lock, Sleep, " +
        "WaitHandle, Other) and findings: io-block, sync-over-async (Task.Wait/Result — the thread waits on work that " +
        "is already asynchronous), thread-pool-blocking, io-category, io-pressure. Use it INSTEAD OF guessing why " +
        "requests are slow or threads are parked; pair it with profile_diagnose or profile_stacks for the live view.")]
    public static async Task<object> Io(
        IIoBlockService io,
        IRoslynWorkspace workspace,
        [Description("Path to the .nettrace file (profile='runtime' or 'diagnose' requests stack walks).")] string tracePath,
        [Description("How many blocking sites to return (1-100).")] int top = 15,
        [Description("Minimum share (percent of blocked time) for a site to be reported.")] double minPercent = 1.0,
        [Description("Resolve frames to file:line using portable PDBs (default true).")] bool resolveSource = true)
    {
        try
        {
            if (!File.Exists(tracePath))
                return new { error = $"Trace not found: {tracePath}" };

            var solutionDirectory = ProfilingToolHelpers.SolutionDirectory(workspace);
            var report = await io.AnalyzeAsync(tracePath, top, minPercent, resolveSource, solutionDirectory);
            return report;
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
