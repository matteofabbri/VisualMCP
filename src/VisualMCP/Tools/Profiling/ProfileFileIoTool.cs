using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling;
using Snitch.Profiling.Analysis;
using Snitch.Profiling.Runtime;
using Snitch.Workspace;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileFileIoTool
{
    [McpServerTool(Name = "profile_file_io"), Description(
        "Who is reading and writing which file, how much and how often — Visual Studio's File I/O tool, natively. " +
        "It opens a real-time ETW session with the Windows kernel file provider (Microsoft-Windows-Kernel-File), " +
        "which is the only source that knows the process behind each read/write, and returns: per-process totals " +
        "(the writer), per-file read/write bytes and operation counts (the target and the pattern), read/write MB/s, " +
        "and findings — file-write-hotspot (one file owns the writes), small-write-storm (thousands of tiny writes: " +
        "buffer them), handle-churn (open/close per operation), write-volume, temp-writes, unresolved-paths, no-file-io. " +
        "Needs Windows and an elevated server (or the Performance Log Users group): a kernel ETW session is privileged, " +
        "exactly like the Visual Studio tool. A kernel session cannot be filtered by pid, so the capture is system-wide " +
        "and the pid/path filters are applied to the events; always pass processId or processName under load. " +
        "This is the physical-write view: for where threads BLOCK in I/O use profile_io, and for which code causes the " +
        "writes use profile_allocations or profile_root_cause.")]
    public static async Task<object> FileIo(
        IFileIoService fileIo,
        IProfiledProcessService processes,
        ProfilingOptions options,
        IRoslynWorkspace workspace,
        [Description("Target process id. Omit with processName to capture the whole machine.")] int? processId = null,
        [Description("Target process name (or a substring); must match exactly one process.")] string? processName = null,
        [Description("How long to capture, in seconds (1-120; default 10). The workload must run inside this window.")] int durationSeconds = 10,
        [Description("Only count paths containing this text (case-insensitive), e.g. 'logs' or '\\\\data\\\\'.")] string? pathFilter = null,
        [Description("How many files and processes to return, worst writer first (1-200).")] int top = 25,
        [Description("Write a Markdown report next to the other profiling artifacts (default false).")] bool writeReport = false,
        [Description("Where to write the Markdown report. Default: <solution>/.visualMCP/profiling/fileio-<process|system>-<time>.md.")] string? outputPath = null)
    {
        try
        {
            int? target = processId;
            if (target is null && !string.IsNullOrWhiteSpace(processName))
            {
                var (resolved, error) = processes.Resolve(null, processName);
                if (resolved is null)
                    return new { error };
                target = resolved.ProcessId;
            }

            var report = await fileIo.CollectAsync(target, durationSeconds, pathFilter, top);

            string? reportPath = null;
            if (writeReport)
            {
                var directory = options.ResolveOutputDirectory(ProfilingToolHelpers.SolutionDirectory(workspace));
                Directory.CreateDirectory(directory);
                var name = target is null ? "system" : $"pid{target}";
                reportPath = Path.Combine(directory, $"fileio-{name}-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                if (!string.IsNullOrWhiteSpace(outputPath))
                    reportPath = Path.GetFullPath(outputPath);
                reportPath = await ProfilingReportWriter.WriteFileIoReportAsync(report, reportPath);
            }

            return new
            {
                report,
                reportPath,
                next = report.Findings.Count == 0
                    ? "No finding fired: run the workload inside the window, or widen the filters."
                    : "Open the top file's writer; profile_allocations or profile_root_cause name the code that writes it.",
            };
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
