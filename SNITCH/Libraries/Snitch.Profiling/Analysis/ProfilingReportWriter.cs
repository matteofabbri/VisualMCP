using System.Globalization;
using System.Text;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

/// <summary>
/// Renders an analysis as Markdown next to the artifact it came from. The report is the artifact
/// a human reads after a long collection; the tool response keeps the structured numbers.
/// </summary>
public static class ProfilingReportWriter
{
    public static async Task<string> WriteTraceReportAsync(
        TraceAnalysis analysis, string path, CancellationToken cancellationToken = default)
    {
        var markdown = BuildTraceMarkdown(analysis);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return Path.GetFullPath(path);
    }

    public static async Task<string> WriteHeapReportAsync(
        HeapSnapshot heap, string path, CancellationToken cancellationToken = default)
    {
        var markdown = BuildHeapMarkdown(heap);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return Path.GetFullPath(path);
    }

    public static async Task<string> WriteStackReportAsync(
        StackSnapshot stacks, string path, CancellationToken cancellationToken = default)
    {
        var markdown = BuildStackMarkdown(stacks);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return Path.GetFullPath(path);
    }

    public static async Task<string> WriteFileIoReportAsync(
        FileIoReport report, string path, CancellationToken cancellationToken = default)
    {
        var markdown = BuildFileIoMarkdown(report);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return Path.GetFullPath(path);
    }

    public static string BuildFileIoMarkdown(FileIoReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# File I/O report");
        sb.AppendLine();
        sb.AppendLine($"- Target: {report.Target}");
        sb.AppendLine(FormattableString.Invariant($"- Window: {report.DurationMs / 1000.0:0.0} s ({report.EventCount:N0} events)"));
        sb.AppendLine(FormattableString.Invariant($"- Read: {FormatIoBytes(report.TotalReadBytes)} in {report.TotalReadOps:N0} operations ({report.ReadMegabytesPerSecond:0.##} MB/s)"));
        sb.AppendLine(FormattableString.Invariant($"- Write: {FormatIoBytes(report.TotalWriteBytes)} in {report.TotalWriteOps:N0} operations ({report.WriteMegabytesPerSecond:0.##} MB/s)"));
        sb.AppendLine($"- Files: {report.DistinctFiles}, processes: {report.DistinctProcesses}");
        if (report.Truncated)
            sb.AppendLine("- Truncated: yes — the event budget was reached, the report is a prefix of the window");
        sb.AppendLine();

        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("No rule fired on the collected data.");
        }
        else
        {
            foreach (var finding in report.Findings)
            {
                sb.AppendLine($"- **[{finding.Severity}] {finding.Title}** — {finding.Evidence}");
                sb.AppendLine($"  {finding.Recommendation}");
                if (finding.SuggestedAction is not null)
                    sb.AppendLine($"  Next: {finding.SuggestedAction}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Processes");
        sb.AppendLine();
        sb.AppendLine("| Process | PID | Write | Read | Write ops | Read ops | Files | Write share |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var process in report.Processes)
            sb.AppendLine(FormattableString.Invariant($"| {process.ProcessName} | {process.ProcessId} | {FormatIoBytes(process.WriteBytes)} | {FormatIoBytes(process.ReadBytes)} | {process.WriteOps:N0} | {process.ReadOps:N0} | {process.Files} | {process.WriteShare:0.0}% |"));
        sb.AppendLine();

        sb.AppendLine("## Files");
        sb.AppendLine();
        sb.AppendLine("| File | Process | Write | Read | Write ops | Read ops | Opens | Closes | Avg write | Write share |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var file in report.Files)
            sb.AppendLine(FormattableString.Invariant($"| `{file.Path}` | {file.ProcessName} ({file.ProcessId}) | {FormatIoBytes(file.WriteBytes)} | {FormatIoBytes(file.ReadBytes)} | {file.WriteOps:N0} | {file.ReadOps:N0} | {file.Opens:N0} | {file.Closes:N0} | {FormatIoBytes((long)file.AverageWriteBytes)} | {file.WriteShare:0.0}% |"));
        sb.AppendLine();

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var warning in report.Warnings)
                sb.AppendLine($"- {warning}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatIoBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => FormattableString.Invariant($"{bytes / 1024.0 / 1024 / 1024:0.0} GB"),
        >= 1024L * 1024 => FormattableString.Invariant($"{bytes / 1024.0 / 1024:0.0} MB"),
        >= 1024 => FormattableString.Invariant($"{bytes / 1024.0:0.0} KB"),
        _ => $"{bytes} B",
    };

    public static async Task<string> WriteDiagnosisReportAsync(
        DiagnosisReport report, string path, CancellationToken cancellationToken = default)
    {
        var markdown = BuildDiagnosisMarkdown(report);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        return Path.GetFullPath(path);
    }

    public static string BuildDiagnosisMarkdown(DiagnosisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Diagnosis report");
        sb.AppendLine();
        sb.AppendLine($"- Process: {report.ProcessName} ({report.ProcessId})");
        sb.AppendLine($"- Started: {report.StartedAt:yyyy-MM-dd HH:mm:ss}, sampled for {report.DurationSeconds:0}s");
        if (report.TracePath is not null)
            sb.AppendLine($"- Trace: `{report.TracePath}`");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("No rule fired on the collected data. Collect a longer window or run the process under load.");
        }
        else
        {
            foreach (var finding in report.Findings.Take(5))
                sb.AppendLine($"- **[{finding.Severity}] {finding.Title}** — {finding.Evidence}");
        }
        sb.AppendLine();

        AppendFindings(sb, report.Findings);

        sb.AppendLine("## Counters");
        sb.AppendLine();
        sb.AppendLine("| Counter | Mean | Max | Last | Unit | Points |");
        sb.AppendLine("|---|---:|---:|---:|---|---:|");
        foreach (var series in report.Counters)
            sb.AppendLine($"| `{series.Name}` | {series.Mean:0.##} | {series.Max:0.##} | {series.Last:0.##} | {series.Unit} | {series.Points} |");
        sb.AppendLine();

        var trace = report.Trace;
        sb.AppendLine("## CPU");
        sb.AppendLine();
        sb.AppendLine($"- {trace.Trace.TotalCpuMs:N0} ms of CPU samples ({trace.Trace.CpuSamples:N0} samples) over {trace.Trace.DurationMs:N0} ms");
        sb.AppendLine();
        if (trace.CpuHotspots.Count > 0)
        {
            sb.AppendLine("| Method | CPU ms | Share | Source |");
            sb.AppendLine("|---|---:|---:|---|");
            foreach (var hotspot in trace.CpuHotspots)
                sb.AppendLine($"| `{hotspot.Method}` | {hotspot.EstimatedCpuMs:N0} | {hotspot.Share:0.0}% | {Cell(hotspot.Source)} |");
            sb.AppendLine();
        }

        if (trace.AllocationSites.Count > 0)
        {
            sb.AppendLine("## Allocation sites");
            sb.AppendLine();
            sb.AppendLine("| Site | Type | MB | Share | Source |");
            sb.AppendLine("|---|---|---:|---:|---|");
            foreach (var site in trace.AllocationSites)
                sb.AppendLine($"| `{site.Site}` | `{site.TypeName}` | {site.SampledBytes / 1024.0 / 1024.0:0.0} | {site.Share:0.0}% | {Cell(site.Source)} |");
            sb.AppendLine();
        }

        if (report.Heap is { } heap)
        {
            sb.AppendLine("## Heap");
            sb.AppendLine();
            sb.AppendLine($"- {heap.ObjectCount:N0} objects, {heap.TotalBytes / 1024.0 / 1024.0:N1} MB, LOH {heap.LohBytes / 1024.0 / 1024.0:N1} MB, {heap.FinalizableObjects:N0} finalizable");
            sb.AppendLine();
            sb.AppendLine("| Type | Count | MB | Share |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var type in heap.TopTypes.Take(10))
                sb.AppendLine($"| `{type.TypeName}` | {type.Count:N0} | {type.Bytes / 1024.0 / 1024.0:N1} | {type.Share:0.0}% |");
            sb.AppendLine();
        }

        if (report.Stacks is { } stacks && stacks.ContendedMonitors.Count > 0)
        {
            sb.AppendLine("## Contended monitors");
            sb.AppendLine();
            foreach (var monitor in stacks.ContendedMonitors)
                sb.AppendLine($"- {monitor.WaitingThreads} waiter(s) on `{monitor.ObjectType}` held by thread {monitor.HolderThread}" +
                              (monitor.HolderStack is null ? "" : $"; holder: `{monitor.HolderStack}`"));
            sb.AppendLine();
        }

        if (report.NextSteps.Count > 0)
        {
            sb.AppendLine("## Next steps");
            sb.AppendLine();
            foreach (var step in report.NextSteps)
                sb.AppendLine($"- {step}");
            sb.AppendLine();
        }

        if (report.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var warning in report.Warnings.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {warning}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildTraceMarkdown(TraceAnalysis analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Profiling report");
        sb.AppendLine();
        sb.AppendLine($"- Trace: `{analysis.Trace.Path}`");
        sb.AppendLine($"- Duration: {analysis.Trace.DurationMs:N0} ms, events: {analysis.Trace.EventCount:N0}, lost: {analysis.Trace.EventsLost}");
        sb.AppendLine($"- CPU samples: {analysis.Trace.CpuSamples:N0} ({analysis.Trace.TotalCpuMs:N0} ms)");
        sb.AppendLine();

        AppendFindings(sb, analysis.Findings);

        if (analysis.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var warning in analysis.Warnings)
                sb.AppendLine($"- {warning}");
            sb.AppendLine();
        }

        sb.AppendLine("## CPU hotspots");
        sb.AppendLine();
        sb.AppendLine("| Method | Samples | Share | ~CPU ms | Source |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var hotspot in analysis.CpuHotspots)
            sb.AppendLine($"| `{hotspot.Method}` | {hotspot.Samples:N0} | {hotspot.Share:0.0}% | {hotspot.EstimatedCpuMs:N0} | {Cell(hotspot.Source)} |");
        sb.AppendLine();

        if (analysis.HotLines.Count > 0)
        {
            sb.AppendLine("## Hot lines");
            sb.AppendLine();
            sb.AppendLine("| Line | Method | Samples | Share | ~CPU ms |");
            sb.AppendLine("|---|---|---:|---:|---:|");
            foreach (var line in analysis.HotLines)
                sb.AppendLine($"| {Cell($"{line.File}:{line.Line}")} | `{line.Method}` | {line.Samples:N0} | {line.Share:0.0}% | {line.EstimatedCpuMs:N0} |");
            sb.AppendLine();
        }

        if (analysis.CpuStacks.Count > 0)
        {
            sb.AppendLine("## Top call stacks");
            sb.AppendLine();
            foreach (var stack in analysis.CpuStacks)
            {
                sb.AppendLine($"- {stack.CpuMs:N0} ms ({stack.Share:0.0}%): `{stack.Stack}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Allocation hotspots (sampled)");
        sb.AppendLine();
        sb.AppendLine("| Type | Sampled MB | Samples | Share |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var allocation in analysis.Allocations)
            sb.AppendLine($"| `{allocation.TypeName}` | {allocation.SampledBytes / 1024.0 / 1024.0:0.0} | {allocation.Samples:N0} | {allocation.Share:0.0}% |");
        sb.AppendLine();

        if (analysis.AllocationSites.Count > 0)
        {
            sb.AppendLine("## Allocation sites (sampled)");
            sb.AppendLine();
            sb.AppendLine("| Site | Type | Sampled MB | Samples | Share | Kind | Source |");
            sb.AppendLine("|---|---|---:|---:|---:|---|---|");
            foreach (var site in analysis.AllocationSites)
                sb.AppendLine($"| `{site.Site}` | `{site.TypeName}` | {site.SampledBytes / 1024.0 / 1024.0:0.0} | " +
                              $"{site.Samples:N0} | {site.Share:0.0}% | {site.Kind} | {Cell(site.Source)} |");
            sb.AppendLine();
        }

        var gc = analysis.Gc;
        sb.AppendLine("## Garbage collection");
        sb.AppendLine();
        sb.AppendLine($"- Gen0/1/2: {gc.Gen0}/{gc.Gen1}/{gc.Gen2}");
        sb.AppendLine($"- Pause total: {gc.TotalPauseMs:N0} ms ({gc.PauseShare:0.0}% of the trace), max {gc.MaxPauseMs:N0} ms");
        sb.AppendLine($"- Heap: {gc.HeapBytes / 1024.0 / 1024.0:N1} MB, LOH {gc.LohBytes / 1024.0 / 1024.0:N1} MB, promoted {gc.PromotedBytes / 1024.0 / 1024.0:N1} MB");
        sb.AppendLine($"- Finalized objects: {gc.FinalizedObjects:N0}");
        sb.AppendLine();
        if (gc.LongestPauses.Count > 0)
        {
            sb.AppendLine("| GC | Gen | ms | Reason | Type |");
            sb.AppendLine("|---:|---:|---:|---|---|");
            foreach (var pause in gc.LongestPauses)
                sb.AppendLine($"| {pause.Index} | {pause.Generation} | {pause.DurationMs:0.0} | {pause.Reason} | {pause.Kind} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Exceptions");
        sb.AppendLine();
        if (analysis.Exceptions.Count == 0)
        {
            sb.AppendLine("None observed.");
        }
        else
        {
            sb.AppendLine("| Type | Count | Sample message | Throw site |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var exception in analysis.Exceptions)
                sb.AppendLine($"| `{exception.Type}` | {exception.Count:N0} | {Cell(exception.SampleMessage)} | {Cell(exception.Source)} |");
        }
        sb.AppendLine();

        if (analysis.Timeline.Count > 1)
        {
            sb.AppendLine("## Timeline");
            sb.AppendLine();
            sb.AppendLine("| Start s | CPU ms | Alloc MB | GC (0/1/2) | Pause ms | Exceptions | Contention ms |");
            sb.AppendLine("|---:|---:|---:|---|---:|---:|---:|");
            foreach (var bucket in analysis.Timeline)
                sb.AppendLine($"| {bucket.StartMs / 1000.0:0.0} | {bucket.CpuMs:N0} | {bucket.SampledAllocationBytes / 1024.0 / 1024.0:N1} | " +
                              $"{bucket.Gen0}/{bucket.Gen1}/{bucket.Gen2} | {bucket.GcPauseMs:N0} | {bucket.Exceptions:N0} | {bucket.ContentionMs:N0} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Contention and thread pool");
        sb.AppendLine();
        sb.AppendLine($"- Contention: {analysis.Contention.Events:N0} events, {analysis.Contention.TotalWaitMs:N0} ms total, max {analysis.Contention.MaxWaitMs:N0} ms");
        sb.AppendLine($"- Thread pool: max active {analysis.ThreadPool.MaxActiveWorkers}, max backlog {analysis.ThreadPool.MaxBacklog:N0}, enqueued {analysis.ThreadPool.Enqueued:N0}, dequeued {analysis.ThreadPool.Dequeued:N0}");
        sb.AppendLine($"- JIT: {analysis.Jit.Methods:N0} methods, {analysis.Jit.TotalJitMs:N0} ms");
        sb.AppendLine();

        if (analysis.Jit.Slowest.Count > 0)
        {
            sb.AppendLine("| Slowest JIT | ms | IL bytes |");
            sb.AppendLine("|---|---:|---:|");
            foreach (var jit in analysis.Jit.Slowest)
                sb.AppendLine($"| `{jit.Method}` | {jit.JitMs:0.0} | {jit.IlSize:N0} |");
            sb.AppendLine();
        }

        if (analysis.NextSteps.Count > 0)
        {
            sb.AppendLine("## Next steps");
            sb.AppendLine();
            foreach (var step in analysis.NextSteps)
                sb.AppendLine($"- {step}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildHeapMarkdown(HeapSnapshot heap)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Managed heap snapshot");
        sb.AppendLine();
        sb.AppendLine($"- Source: `{heap.Source}`");
        sb.AppendLine($"- Runtime: {heap.RuntimeVersion} ({(heap.ServerGc ? "server" : "workstation")} GC), threads: {heap.Threads}");
        sb.AppendLine($"- Objects: {heap.ObjectCount:N0}, size: {heap.TotalBytes / 1024.0 / 1024.0:N1} MB");
        sb.AppendLine($"- LOH: {heap.LohBytes / 1024.0 / 1024.0:N1} MB, pinned: {heap.PinnedBytes / 1024.0 / 1024.0:N1} MB, free: {heap.FreeBytes / 1024.0 / 1024.0:N1} MB");
        sb.AppendLine($"- Finalizable: {heap.FinalizableObjects:N0}{(heap.Truncated ? " (heap walk truncated)" : "")}");
        sb.AppendLine();

        sb.AppendLine("## Types by size");
        sb.AppendLine();
        sb.AppendLine("| Type | Count | MB | Share |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var type in heap.TopTypes)
            sb.AppendLine($"| `{type.TypeName}` | {type.Count:N0} | {type.Bytes / 1024.0 / 1024.0:N1} | {type.Share:0.0}% |");
        sb.AppendLine();

        if (heap.Samples.Count > 0)
        {
            sb.AppendLine("## Objects");
            sb.AppendLine();
            sb.AppendLine("| Address | Type | Bytes | Finalizable |");
            sb.AppendLine("|---|---|---:|---|");
            foreach (var sample in heap.Samples)
                sb.AppendLine($"| `{sample.Address}` | `{sample.TypeName}` | {sample.Bytes:N0} | {sample.Finalizable} |");
            sb.AppendLine();
        }

        if (heap.DuplicateStrings.Count > 0)
        {
            sb.AppendLine("## Duplicated strings");
            sb.AppendLine();
            sb.AppendLine("| Value | Copies | Wasted bytes |");
            sb.AppendLine("|---|---:|---:|");
            foreach (var duplicate in heap.DuplicateStrings)
                sb.AppendLine($"| `{Cell(duplicate.Value)}` | {duplicate.Count:N0} | {duplicate.WastedBytes:N0} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildStackMarkdown(StackSnapshot stacks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Managed stack snapshot");
        sb.AppendLine();
        sb.AppendLine($"- Source: `{stacks.Source}`");
        sb.AppendLine($"- Runtime: {stacks.RuntimeVersion}, threads: {stacks.Threads.Count}{(stacks.ThreadsTruncated > 0 ? $" (+{stacks.ThreadsTruncated} not shown)" : "")}");
        sb.AppendLine();

        if (stacks.ContendedMonitors.Count > 0)
        {
            sb.AppendLine("## Contended monitors");
            sb.AppendLine();
            foreach (var monitor in stacks.ContendedMonitors)
            {
                sb.AppendLine($"- {monitor.WaitingThreads} waiter(s) on `{monitor.ObjectType}` held by thread {monitor.HolderThread}" +
                              (monitor.HolderStack is null ? "" : $"; holder top frame: `{monitor.HolderStack}`"));
            }
            sb.AppendLine();
        }

        foreach (var thread in stacks.Threads)
        {
            sb.AppendLine($"## Thread {thread.ManagedThreadId} (OS {thread.OsThreadId}) — {thread.State}{(thread.IsFinalizer ? ", finalizer" : "")}{(thread.IsGc ? ", GC" : "")}");
            sb.AppendLine();
            if (thread.ExceptionType is not null)
                sb.AppendLine($"Exception in flight: `{thread.ExceptionType}`");
            sb.AppendLine();
            if (thread.Frames.Count == 0)
            {
                sb.AppendLine("_No managed frames._");
                sb.AppendLine();
                continue;
            }

            foreach (var frame in thread.Frames)
                sb.AppendLine($"{new string(' ', Math.Min(frame.Depth, 30) * 2)}{frame.Depth}. `{frame.Method}`" +
                              (frame.Source is null ? "" : $" — {frame.Source}"));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendFindings(StringBuilder sb, IReadOnlyList<ProfilingFinding> findings)
    {
        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("No rule fired on this data.");
            sb.AppendLine();
            return;
        }

        foreach (var finding in findings)
        {
            sb.AppendLine($"### [{finding.Severity}] {finding.Title}");
            sb.AppendLine();
            sb.AppendLine(finding.Evidence);
            sb.AppendLine();
            sb.AppendLine($"**Recommendation:** {finding.Recommendation}");
            if (finding.SuggestedAction is not null)
                sb.AppendLine($"**Next:** {finding.SuggestedAction}");
            if (finding.Source is not null)
                sb.AppendLine($"**Source:** {finding.Source}");
            sb.AppendLine();
        }
    }

    private static string Cell(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Replace("|", "\\|", StringComparison.Ordinal);
}
