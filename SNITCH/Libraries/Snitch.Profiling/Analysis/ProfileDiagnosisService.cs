using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace Snitch.Profiling.Analysis;

public interface IProfileDiagnosisService
{
    Task<DiagnosisReport> DiagnoseAsync(
        int processId, string processName, int durationSeconds, bool includeHeap, bool includeStacks,
        int top, string outputDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// The multi-signal entry point: counters, a trace, the live stacks and (optionally) a heap
/// snapshot are collected in one call, each analyzed by the same rule engine, then correlated.
/// The correlation is the part a single tool cannot give: "cpu-usage is 96% and method X owns 71%
/// of the samples" is a different statement from either measurement alone.
/// </summary>
public sealed class ProfileDiagnosisService : IProfileDiagnosisService
{
    private readonly ICounterCollectionService _counters;
    private readonly ITraceCollectionService _traces;
    private readonly ITraceAnalysisService _analysis;
    private readonly IManagedHeapService _heap;
    private readonly IManagedStackService _stacks;
    private readonly IProfilingAdvisor _advisor;
    private readonly ProfilingOptions _options;

    public ProfileDiagnosisService(
        ICounterCollectionService counters,
        ITraceCollectionService traces,
        ITraceAnalysisService analysis,
        IManagedHeapService heap,
        IManagedStackService stacks,
        IProfilingAdvisor advisor,
        ProfilingOptions options)
    {
        _counters = counters;
        _traces = traces;
        _analysis = analysis;
        _heap = heap;
        _stacks = stacks;
        _advisor = advisor;
        _options = options;
    }

    public async Task<DiagnosisReport> DiagnoseAsync(
        int processId, string processName, int durationSeconds, bool includeHeap, bool includeStacks,
        int top, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));

        var duration = Math.Clamp(durationSeconds <= 0 ? _options.DefaultDurationSeconds : durationSeconds,
            3, _options.MaxDurationSeconds);
        var startedAt = DateTime.Now;
        var warnings = new List<string>();

        var counters = await _counters.CollectAsync(
            processId, counterFilter: null, durationSeconds: Math.Min(5, duration),
            intervalSeconds: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
        warnings.AddRange(counters.Warnings);

        var safeName = string.Concat(processName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var tracePath = Path.Combine(
            outputDirectory, $"diagnosis-trace-{safeName}-{processId}-{startedAt:yyyyMMdd-HHmmss}.nettrace");

        var collected = await _traces.CollectAsync(
            processId, tracePath, "diagnose", customProviders: null, duration, bufferMb: 256,
            requestStackwalk: true, cancellationToken).ConfigureAwait(false);

        var analysis = await _analysis.AnalyzeAsync(
            collected.Path, top, resolveSource: true, deep: true, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(analysis.Warnings);

        HeapSnapshot? heap = null;
        if (includeHeap)
        {
            try
            {
                heap = await _heap.CaptureAsync(processId, top, focusType: null, includeDuplicateStrings: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"The heap snapshot failed: {ex.Message}");
            }
        }

        StackSnapshot? stacks = null;
        if (includeStacks)
        {
            try
            {
                stacks = await _stacks.CaptureAsync(processId, maxFrames: 40, includeRuntimeFrames: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"The stack snapshot failed: {ex.Message}");
            }
        }

        var findings = new List<ProfilingFinding>();
        findings.AddRange(analysis.Findings);
        findings.AddRange(_advisor.Analyze(counters));
        if (heap is not null) findings.AddRange(_advisor.Analyze(heap));
        if (stacks is not null) findings.AddRange(_advisor.Analyze(stacks));

        findings.AddRange(Correlate(counters, analysis, heap, stacks));
        findings = findings
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => SeverityRank(f.Severity)).First())
            .OrderByDescending(f => SeverityRank(f.Severity))
            .ToList();

        var nextSteps = _advisor.NextSteps(findings).ToList();
        if (heap is null)
            nextSteps.Add("profile_heap to see which type owns the managed heap.");
        if (heap is not null)
            nextSteps.Add("After a change, profile_compare the two heap snapshots (profile_heap writeJson=true) or the two traces.");

        Directory.CreateDirectory(outputDirectory);
        var reportPath = Path.Combine(
            outputDirectory, $"diagnosis-{safeName}-{processId}-{startedAt:yyyyMMdd-HHmmss}.md");

        var report = new DiagnosisReport(
            processName, processId, startedAt, duration,
            collected.Path, reportPath, counters.Series, analysis, heap, stacks,
            findings, nextSteps.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(), warnings);

        await ProfilingReportWriter.WriteDiagnosisReportAsync(report, reportPath, cancellationToken)
            .ConfigureAwait(false);

        return report;
    }

    private static IEnumerable<ProfilingFinding> Correlate(
        CounterCollectionResult counters,
        TraceAnalysis analysis,
        HeapSnapshot? heap,
        StackSnapshot? stacks)
    {
        var cpu = counters.Series.FirstOrDefault(s => s.Name.Equals("cpu-usage", StringComparison.OrdinalIgnoreCase));
        var hotspot = analysis.CpuHotspots.FirstOrDefault();
        if (cpu is not null && cpu.Max >= 80 && hotspot is not null)
            yield return new ProfilingFinding(
                "cpu-bound", cpu.Max >= 95 ? "critical" : "warning",
                $"CPU-bound in {ShortName(hotspot.Method)}",
                $"cpu-usage peaked at {cpu.Max:0.0}% while {hotspot.Method} owned {hotspot.Share:0.0}% of the CPU samples " +
                $"({hotspot.EstimatedCpuMs:N0} ms).",
                "The method is both hot and on the critical path of a saturated process: optimise it first, then re-measure.",
                "profile_calltree to see which caller spends that time.",
                hotspot.Source);

        var gcTime = counters.Series.FirstOrDefault(s => s.Name.Equals("time-in-gc", StringComparison.OrdinalIgnoreCase));
        if (gcTime is not null && gcTime.Mean >= 10 && analysis.Gc.Gen2 > 0)
        {
            var allocations = analysis.Allocations.FirstOrDefault();
            yield return new ProfilingFinding(
                "gc-bound", gcTime.Mean >= 25 ? "critical" : "warning",
                "GC is on the critical path",
                $"time-in-gc averages {gcTime.Mean:0.0}% with {analysis.Gc.Gen2} gen-2 collections and " +
                $"{analysis.Gc.SampledAllocationBytes / 1024.0 / 1024.0:N0} MB sampled allocations" +
                (allocations is null ? "." : $", {AllocationShorthand(allocations)}."),
                "Reduce allocation volume or lifetime; the allocation site is the actionable target, not the GC itself.",
                "profile_allocations on the collected trace.",
                analysis.AllocationSites.FirstOrDefault()?.Source);
        }

        var queue = counters.Series.FirstOrDefault(s => s.Name.Equals("threadpool-queue-length", StringComparison.OrdinalIgnoreCase));
        var blocked = stacks is null ? 0 : stacks.Threads.Count(t => t.Frames.Count > 0 && IsBlocking(t.Frames[0].Method));
        if ((queue is not null && queue.Max >= 1000) || blocked >= 8)
            yield return new ProfilingFinding(
                "pool-starvation", "warning",
                "The thread pool is starved",
                $"thread-pool queue peaked at {(queue?.Max ?? 0):N0} items; {blocked} thread(s) are blocked in synchronous waits.",
                "Remove the blocking call (Task.Wait/.Result/Thread.Sleep/sync I/O) from the pool threads; make the path async.",
                "profile_stacks to read the blocked stacks.",
                stacks?.Threads.FirstOrDefault(t => t.Frames.Count > 0 && IsBlocking(t.Frames[0].Method))?.Frames[0].Source);

        var heapTop = heap?.TopTypes.FirstOrDefault();
        if (heapTop is not null && heapTop.Share >= 30)
            yield return new ProfilingFinding(
                "heap-dominant", "warning",
                $"{ShortName(heapTop.TypeName)} is {heapTop.Share:0}% of the heap",
                $"{heapTop.Count:N0} instances, {heapTop.Bytes / 1024.0 / 1024.0:N1} MB of {heap!.TotalBytes / 1024.0 / 1024.0:N1} MB.",
                "If that type grows between runs, it is the leak/cache to bound; take a dump to inspect its roots.",
                "profile_heap focusType='" + heapTop.TypeName + "'.",
                null);
    }

    private static bool IsBlocking(string method) =>
        method.Contains("Monitor.Wait", StringComparison.Ordinal) ||
        method.Contains("Task.Wait", StringComparison.Ordinal) ||
        method.Contains("Task.Result", StringComparison.Ordinal) ||
        method.Contains("Thread.Sleep", StringComparison.Ordinal) ||
        method.Contains("SemaphoreSlim.Wait", StringComparison.Ordinal) ||
        method.Contains("WaitHandle.Wait", StringComparison.Ordinal);

    private static string AllocationShorthand(AllocationHotspot allocation) =>
        $"{allocation.TypeName} is {allocation.SampledBytes / 1024.0 / 1024.0:N0} MB ({allocation.Share:0}%) of them";

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1,
    };

    private static string ShortName(string fullName) => FrameNames.ShortName(fullName);
}
