using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileDumpTool
{
    [McpServerTool(Name = "profile_dump"), Description(
        "Collect a dump of a live .NET process and/or analyse a dump with ClrMD — the native equivalent of " +
        "'dotnet-dump collect' plus the common SOS questions, with no external tool. " +
        "action='collect' asks the runtime to write the dump (dumpType: 'normal', 'withheap' (default), 'triage', " +
        "'full'); action='analyze' opens an existing .dmp and returns the heap breakdown (types, LOH, finalizable, " +
        "duplicate strings) and, unless includeStacks=false, every thread's managed stack plus contended monitors. " +
        "Analysis needs the runtime matching the dump on this machine; if the DAC cannot be loaded the error says so. " +
        "Use this when you need a frozen picture of a process that is hung, leaking or crashing.")]
    public static async Task<object> Dump(
        IDumpService dumps,
        IProfiledProcessService processes,
        [Description("'collect' (default) or 'analyze'.")] string action = "collect",
        [Description("Target process id for action='collect'. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring) for action='collect'.")] string? processName = null,
        [Description("Where to write the dump for action='collect'. Default: <solution>/.visualMCP/profiling/dump-<process>-<pid>-<time>.dmp.")] string? outputPath = null,
        [Description("Dump kind for action='collect': normal, withheap (default), triage or full.")] string dumpType = "withheap",
        [Description("Existing .dmp for action='analyze'.")] string? dumpPath = null,
        [Description("How many heap types to return when analysing (1-200).")] int top = 25,
        [Description("Only return heap objects whose type name contains this text (case-insensitive).")] string? focusType = null,
        [Description("Analyse the managed stacks of the dump too (default true).")] bool includeStacks = true,
        [Description("Maximum frames per thread when analysing stacks.")] int maxFrames = 40)
    {
        try
        {
            if (string.Equals(action, "analyze", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(dumpPath))
                    return new { error = "action='analyze' needs dumpPath." };

                var analysis = await dumps.AnalyzeAsync(dumpPath, top, focusType, includeStacks, maxFrames);
                return new { action = "analyzed", analysis };
            }

            if (!string.IsNullOrWhiteSpace(dumpPath))
                return new { error = "dumpPath is only used with action='analyze'. For action='collect' use outputPath." };

            var (target, error) = processes.Resolve(processId, processName);
            if (target is null)
                return new { error };

            var result = await dumps.CollectAsync(target.ProcessId, outputPath, dumpType);
            return new
            {
                action = "collected",
                result,
                next = $"profile_dump action='analyze' dumpPath='{result.Path}'",
            };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
