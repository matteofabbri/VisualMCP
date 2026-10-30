using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Snitch.Workspace;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileHeapTool
{
    [McpServerTool(Name = "profile_heap"), Description(
        "Take a managed heap snapshot of a live .NET process — the native equivalent of 'dotnet-gcdump', with no " +
        ".gcdump file and no external tool. The process is paused only while its memory is copied; the walk happens " +
        "on the copy. Returns object count/size per type (the information you read a gcdump for), LOH and pinned " +
        "segment bytes, free bytes, finalizable objects, the ten largest objects, and duplicated strings with their " +
        "wasted bytes. Use focusType (e.g. 'MyApp.Order' or 'System.Byte[]') to get sample addresses for one type. " +
        "Rule-based findings flag a type dominating the heap, LOH pressure, a large finalizer queue and duplicate " +
        "strings. When one type dominates, follow with profile_retention focusType=<that type> to get the root chains " +
        "and the referrer types that keep it alive. Use profile_dump when you need a frozen snapshot you can analyse repeatedly.")]
    public static async Task<object> Heap(
        IManagedHeapService heap,
        IProfiledProcessService processes,
        IProfilingAdvisor advisor,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Target process id. Omit if processName is given.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("How many types to return, sorted by size (1-200).")] int top = 25,
        [Description("Only return objects whose type name contains this text (case-insensitive), e.g. 'System.Byte[]'.")] string? focusType = null,
        [Description("Scan string values to report duplicated strings (slower on a large heap).")] bool includeDuplicateStrings = true,
        [Description("Write the snapshot as JSON so profile_compare kind='heap' can diff two snapshots later.")] bool writeJson = false,
        [Description("Write a Markdown report under <solution>/.visualMCP/profiling and return its path.")] bool writeReport = false)
    {
        try
        {
            var (target, error) = processes.Resolve(processId, processName);
            if (target is null)
                return new { error };

            var snapshot = await heap.CaptureAsync(target.ProcessId, top, focusType, includeDuplicateStrings);
            var findings = advisor.Analyze(snapshot);

            string? report = null;
            if (writeReport)
            {
                var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
                var path = Path.Combine(directory, $"heap-{target.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                report = await ProfilingReportWriter.WriteHeapReportAsync(snapshot, path);
            }

            string? json = null;
            if (writeJson)
            {
                var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"heap-{target.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                    snapshot, new JsonSerializerOptions { WriteIndented = true }));
                json = Path.GetFullPath(path);
            }

            return new { heap = snapshot, findings, report, json };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
