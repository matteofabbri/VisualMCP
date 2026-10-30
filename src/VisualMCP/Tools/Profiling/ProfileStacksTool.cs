using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileStacksTool
{
    [McpServerTool(Name = "profile_stacks"), Description(
        "Capture the managed stack of every thread of a live .NET process — the native equivalent of 'dotnet-stack " +
        "report', with no external tool. The process is suspended only for the walk. Each frame is resolved to " +
        "file:line when a PDB is available. The response also lists contended monitors: for every lock with waiting " +
        "threads it names the holding thread and its top frame, which is the fastest way to diagnose a hang or a " +
        "lock convoy. Also carries rule-based findings (blocked thread-pool threads, exception in flight).")]
    public static async Task<object> Stacks(
        IManagedStackService stacks,
        IProfiledProcessService processes,
        IProfilingAdvisor advisor,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Target process id. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("Maximum frames per thread (1-300).")] int maxFrames = 40,
        [Description("Include runtime/helper frames (noisy; off by default).")] bool includeRuntimeFrames = false,
        [Description("Write a Markdown report under <solution>/.visualMCP/profiling and return its path.")] bool writeReport = false)
    {
        try
        {
            var (target, error) = processes.Resolve(processId, processName);
            if (target is null)
                return new { error };

            var snapshot = await stacks.CaptureAsync(target.ProcessId, maxFrames, includeRuntimeFrames);
            var findings = advisor.Analyze(snapshot);

            string? report = null;
            if (writeReport)
            {
                var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
                var path = Path.Combine(directory, $"stacks-{target.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                report = await ProfilingReportWriter.WriteStackReportAsync(snapshot, path);
            }

            return new { snapshot, findings, report };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
