using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface ITraceAnalysisService
{
    Task<TraceAnalysis> AnalyzeAsync(
        string tracePath, int top, bool resolveSource, bool deep = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a .nettrace file and answers the questions a profiler report should: which methods burn
/// CPU, where allocations are made, what the GC is doing, which exceptions are thrown and from
/// where, which locks block and whether the thread pool starved.
///
/// Two layers are combined. A direct EventPipe pass supplies GC, thread-pool, contention and JIT
/// data cheaply. The deeper layer (CPU samples, allocation sites, exception throw sites) needs the
/// EventPipe → ETLX conversion, because a .nettrace attaches call stacks to the event record rather
/// than emitting them as events; <see cref="ITraceStackReader"/> owns that conversion and caches the
/// resulting model, so analyze, call-tree, allocation-site, compare and diagnose share one pass.
/// </summary>
public sealed class TraceAnalysisService : ITraceAnalysisService
{
    private readonly IProfilingAdvisor _advisor;
    private readonly ITraceStackReader _stackReader;

    public TraceAnalysisService(ProfilingOptions options, IProfilingAdvisor advisor, ITraceStackReader stackReader)
    {
        _advisor = advisor;
        _stackReader = stackReader;
    }

    public Task<TraceAnalysis> AnalyzeAsync(
        string tracePath, int top, bool resolveSource, bool deep = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        return Task.Run(() => Analyze(tracePath, top, resolveSource, deep), cancellationToken);
    }

    private TraceAnalysis Analyze(string tracePath, int top, bool resolveSource, bool deep)
    {
        top = Math.Clamp(top <= 0 ? 25 : top, 1, 200);
        var accumulator = new Accumulator();

        try
        {
            using var source = new EventPipeEventSource(tracePath);
            Subscribe(source, accumulator);
            source.Process();
            accumulator.EventsLost = source.EventsLost;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Could not parse the trace '{tracePath}': {ex.Message}. " +
                "A trace that was stopped abruptly can be truncated; collect it again with profile_collect.", ex);
        }

        var durationMs = accumulator.LastMs >= accumulator.FirstMs
            ? accumulator.LastMs - accumulator.FirstMs
            : 0;

        var warnings = new List<string>();
        TraceStackModel? model = null;

        var wantsDeep = deep &&
            (accumulator.SampleEvents > 0 || accumulator.AllocationTicks > 0 || accumulator.ExceptionCount > 0);

        if (wantsDeep)
        {
            try
            {
                model = _stackReader.Read(tracePath, resolveSource);
                warnings.AddRange(model.Warnings);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                warnings.Add($"The deep trace data could not be read: {ex.Message}");
            }
        }
        else if (!deep)
        {
            warnings.Add("deep=false: CPU samples, allocation sites and exception throw sites were not extracted.");
        }

        var cpu = BuildCpu(model, top);
        if (model is not null && cpu.Samples == 0 && accumulator.SampleEvents > 0)
            warnings.Add(
                $"{accumulator.SampleEvents:N0} thread-sample events were found but none carried a resolvable managed stack. " +
                "The process may have been idle or blocked: collect while it is under load, or use profile_stacks to see the blocked threads.");

        var (allocationSites, allocations, sampledBytes, allocationTruncated) =
            BuildAllocations(model, accumulator, top);
        if (allocationTruncated)
            warnings.Add("The allocation samples were capped; totals are a lower bound.");
        if (model is not null && model.Allocations.Count > 0 && allocationSites.All(s => s.Site == "(no allocation stack)"))
            warnings.Add(
                "Allocation ticks were recorded without call stacks. Collect with requestStackwalk=true and the gc preset " +
                "(Verbose) to attribute allocations to a method.");

        var exceptions = BuildExceptions(model, accumulator, top);
        var gc = BuildGcSummary(accumulator, durationMs);
        var contention = new ContentionSummary(
            accumulator.ContentionEvents,
            Math.Round(accumulator.ContentionTotalMs, 1),
            Math.Round(accumulator.ContentionMaxMs, 1),
            accumulator.ContentionKinds
                .Select(kv => new ContentionLock(kv.Key, kv.Value.Waits, Math.Round(kv.Value.Total, 1), Math.Round(kv.Value.Max, 1)))
                .OrderByDescending(l => l.TotalWaitMs)
                .ToList());

        var threadPool = new ThreadPoolSummary(
            accumulator.MaxActiveWorkers,
            accumulator.MinActiveWorkers == int.MaxValue ? 0 : accumulator.MinActiveWorkers,
            accumulator.Enqueued,
            accumulator.Dequeued,
            accumulator.MaxBacklog,
            accumulator.AdjustmentSamples);

        var jit = new JitSummary(
            accumulator.JitMethods,
            Math.Round(accumulator.TotalJitMs, 1),
            accumulator.JitTotals
                .Select(kv => new JitHotspot(kv.Value.Name, Math.Round(kv.Value.Ms, 1), kv.Value.IlSize))
                .OrderByDescending(j => j.JitMs)
                .Take(10)
                .ToList());

        var timeline = BuildTimeline(accumulator, model, durationMs);

        var info = new TraceInfo(
            Path.GetFullPath(tracePath),
            SafeFileSize(tracePath),
            Math.Round(durationMs, 1),
            accumulator.EventCount,
            accumulator.EventsLost,
            cpu.Samples,
            Math.Round(cpu.TotalMs, 1));

        var analysis = new TraceAnalysis(
            info, cpu.Hotspots, cpu.Stacks, allocationSites, allocations, gc, exceptions,
            contention, threadPool, jit, timeline, warnings, [], [])
        {
            HotLines = cpu.Lines,
        };

        var findings = _advisor.Analyze(analysis);
        return analysis with
        {
            Findings = findings,
            NextSteps = _advisor.NextSteps(findings),
        };
    }

    private static void Subscribe(EventPipeEventSource source, Accumulator accumulator)
    {
        source.AllEvents += data =>
        {
            accumulator.EventCount++;
            if (data.ProviderGuid == SampleProfilerTraceEventParser.ProviderGuid)
                accumulator.SampleEvents++;
            if (data.TimeStampRelativeMSec < accumulator.FirstMs) accumulator.FirstMs = data.TimeStampRelativeMSec;
            if (data.TimeStampRelativeMSec > accumulator.LastMs) accumulator.LastMs = data.TimeStampRelativeMSec;
        };

        source.Clr.GCStart += data =>
            accumulator.GcStarts[data.Count] = (data.TimeStampRelativeMSec, data.Depth, data.Reason.ToString(), data.Type.ToString());

        source.Clr.GCStop += data =>
        {
            if (!accumulator.GcStarts.Remove(data.Count, out var start))
                return;

            var duration = Math.Max(0, data.TimeStampRelativeMSec - start.Time);
            accumulator.Pauses.Add(new GcPauseInfo(
                data.Count, start.Time, start.Generation, Math.Round(duration, 2), start.Reason, start.Kind));

            switch (start.Generation)
            {
                case 0: accumulator.Gen0++; break;
                case 1: accumulator.Gen1++; break;
                default: accumulator.Gen2++; break;
            }
        };

        source.Clr.GCHeapStats += data =>
        {
            accumulator.HeapBytes = data.TotalHeapSize != 0
                ? data.TotalHeapSize
                : data.GenerationSize0 + data.GenerationSize1 + data.GenerationSize2 + data.GenerationSize3 + data.GenerationSize4;
            accumulator.LohBytes = data.GenerationSize3 + data.GenerationSize4;
            accumulator.PromotedBytes = data.TotalPromoted != 0
                ? data.TotalPromoted
                : data.TotalPromotedSize0 + data.TotalPromotedSize1 + data.TotalPromotedSize2 + data.TotalPromotedSize3;
        };

        source.Clr.GCFinalizeObject += _ => accumulator.FinalizedObjects++;

        source.Clr.GCAllocationTick += data =>
        {
            accumulator.AllocationTicks++;
            var type = string.IsNullOrWhiteSpace(data.TypeName) ? "<unknown>" : data.TypeName;
            var bytes = data.AllocationAmount64 > 0 ? data.AllocationAmount64 : data.ObjectSize;
            if (bytes <= 0)
                return;

            accumulator.SampledAllocBytes += bytes;
            var current = accumulator.Allocs.TryGetValue(type, out var stats) ? stats : default;
            accumulator.Allocs[type] = (current.Bytes + bytes, current.Samples + 1);
        };

        source.Clr.ExceptionStart += data =>
        {
            accumulator.ExceptionCount++;
            var type = string.IsNullOrWhiteSpace(data.ExceptionType) ? "<unknown>" : data.ExceptionType;
            var current = accumulator.Exceptions.TryGetValue(type, out var stats) ? stats : default;
            accumulator.Exceptions[type] = (current.Count + 1,
                current.Message ?? (string.IsNullOrWhiteSpace(data.ExceptionMessage) ? null : data.ExceptionMessage));
        };

        source.Clr.ContentionStop += data =>
        {
            var ms = Math.Max(0, data.DurationNs / 1_000_000.0);
            accumulator.ContentionEvents++;
            accumulator.ContentionTotalMs += ms;
            if (ms > accumulator.ContentionMaxMs) accumulator.ContentionMaxMs = ms;
            if (accumulator.ContentionPoints.Count < 50_000)
                accumulator.ContentionPoints.Add((data.TimeStampRelativeMSec, ms));

            var kind = data.ContentionFlags.ToString();
            var current = accumulator.ContentionKinds.TryGetValue(kind, out var stats) ? stats : default;
            accumulator.ContentionKinds[kind] = (current.Waits + 1, current.Total + ms, Math.Max(current.Max, ms));
        };

        source.Clr.ThreadPoolWorkerThreadStart += data => accumulator.ObserveWorkers(data.ActiveWorkerThreadCount);
        source.Clr.ThreadPoolWorkerThreadStop += data => accumulator.ObserveWorkers(data.ActiveWorkerThreadCount);
        source.Clr.ThreadPoolWorkerThreadWait += data => accumulator.ObserveWorkers(data.ActiveWorkerThreadCount);
        source.Clr.ThreadPoolWorkerThreadAdjustmentSample += _ => accumulator.AdjustmentSamples++;
        source.Clr.ThreadPoolEnqueue += _ =>
        {
            accumulator.Enqueued++;
            var backlog = accumulator.Enqueued - accumulator.Dequeued;
            if (backlog > accumulator.MaxBacklog) accumulator.MaxBacklog = backlog;
        };
        source.Clr.ThreadPoolDequeue += _ => accumulator.Dequeued++;

        source.Clr.MethodJittingStarted += data =>
            accumulator.JitStarts[data.MethodID] = (
                data.TimeStampRelativeMSec, data.MethodILSize,
                DisplayName(data.MethodNamespace, data.MethodName));

        source.Clr.MethodLoadVerbose += accumulator.CompleteJit;
    }

    private static CpuResult BuildCpu(TraceStackModel? model, int top)
    {
        if (model is null || model.CpuSamples.Count == 0)
            return new CpuResult([], [], [], 0, 0);

        var byMethod = new Dictionary<string, (long Samples, double Ms, string? Source)>(StringComparer.Ordinal);
        var byLine = new Dictionary<(string File, int Line, string Method), (long Samples, double Ms)>();
        var byStack = new Dictionary<string, double>(StringComparer.Ordinal);
        double totalMs = 0;

        foreach (var sample in model.CpuSamples)
        {
            if (sample.Frames.Count == 0)
                continue;

            var leaf = sample.Frames[0];
            totalMs += sample.MetricMs;

            var current = byMethod.TryGetValue(leaf.Method, out var stats) ? stats : default;
            byMethod[leaf.Method] = (current.Samples + 1, current.Ms + sample.MetricMs, current.Source ?? leaf.Source);

            // Self time per line: only samples the IL→native map and the PDB could place carry a
            // line, so a method without symbols does not dump its whole cost on the first line.
            if (leaf.File is not null && leaf.Line is int line)
            {
                var lineKey = (leaf.File, line, leaf.Method);
                var lineStats = byLine.TryGetValue(lineKey, out var existing) ? existing : default;
                byLine[lineKey] = (lineStats.Samples + 1, lineStats.Ms + sample.MetricMs);
            }

            var names = sample.Frames.Take(10).Select(f => f.Method);
            var key = string.Join(" <- ", names);
            byStack[key] = (byStack.TryGetValue(key, out var stackMs) ? stackMs : 0) + sample.MetricMs;
        }

        var total = Math.Max(1.0, totalMs);
        var hotspots = byMethod
            .OrderByDescending(kv => kv.Value.Ms)
            .Take(top)
            .Select(kv => new CpuHotspot(
                kv.Key, kv.Value.Source, kv.Value.Samples,
                Math.Round(kv.Value.Ms * 100.0 / total, 2),
                (long)Math.Round(kv.Value.Ms)))
            .ToList();

        var lines = byLine
            .OrderByDescending(kv => kv.Value.Ms)
            .ThenBy(kv => kv.Key.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key.Line)
            .Take(Math.Min(top, 25))
            .Select(kv => new CpuLineHotspot(
                kv.Key.File, kv.Key.Line, kv.Key.Method, kv.Value.Samples,
                Math.Round(kv.Value.Ms * 100.0 / total, 2),
                (long)Math.Round(kv.Value.Ms)))
            .ToList();

        var stacks = byStack
            .OrderByDescending(kv => kv.Value)
            .Take(Math.Min(top, 15))
            .Select(kv => new CpuStackHotspot(
                kv.Key, Math.Round(kv.Value, 1), Math.Round(kv.Value * 100.0 / total, 2)))
            .ToList();

        return new CpuResult(hotspots, stacks, lines, model.CpuSamples.Count, totalMs);
    }

    private static (IReadOnlyList<AllocationSiteHotspot> Sites, IReadOnlyList<AllocationHotspot> ByType,
        long TotalBytes, bool Truncated) BuildAllocations(
        TraceStackModel? model, Accumulator accumulator, int top)
    {
        if (model is null || model.Allocations.Count == 0)
        {
            // The fast path still has the per-type totals from the EventPipe pass; there are no sites.
            var fallback = accumulator.Allocs
                .Select(kv => new AllocationHotspot(
                    kv.Key, kv.Value.Bytes, kv.Value.Samples,
                    accumulator.SampledAllocBytes == 0 ? 0 : Math.Round(kv.Value.Bytes * 100.0 / accumulator.SampledAllocBytes, 2)))
                .OrderByDescending(a => a.SampledBytes)
                .Take(top)
                .ToList();
            return ([], fallback, accumulator.SampledAllocBytes, false);
        }

        var totalBytes = model.Allocations.Sum(a => a.Bytes);
        var total = Math.Max(1L, totalBytes);

        var sites = new Dictionary<(string Site, string Type), (long Bytes, long Count, string? Source, string Kind)>();
        var types = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);

        foreach (var allocation in model.Allocations)
        {
            var leaf = allocation.Frames.Count > 0 ? allocation.Frames[0] : null;
            var site = leaf?.Method ?? "(no allocation stack)";
            var key = (site, allocation.TypeName);
            var current = sites.TryGetValue(key, out var stats) ? stats : default;
            sites[key] = (current.Bytes + allocation.Bytes, current.Count + 1, current.Source ?? leaf?.Source, allocation.Kind);

            var typeStats = types.TryGetValue(allocation.TypeName, out var ts) ? ts : default;
            types[allocation.TypeName] = (typeStats.Bytes + allocation.Bytes, typeStats.Count + 1);
        }

        var topSites = sites
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new AllocationSiteHotspot(
                kv.Key.Site, kv.Value.Source, kv.Key.Type,
                kv.Value.Bytes, kv.Value.Count,
                Math.Round(kv.Value.Bytes * 100.0 / total, 2),
                kv.Value.Kind))
            .ToList();

        var byType = types
            .Select(kv => new AllocationHotspot(
                kv.Key, kv.Value.Bytes, kv.Value.Count,
                Math.Round(kv.Value.Bytes * 100.0 / total, 2)))
            .OrderByDescending(a => a.SampledBytes)
            .Take(top)
            .ToList();

        return (topSites, byType, totalBytes, model.Truncated);
    }

    private static IReadOnlyList<ExceptionHotspot> BuildExceptions(
        TraceStackModel? model, Accumulator accumulator, int top)
    {
        if (model is null || model.Exceptions.Count == 0)
        {
            return accumulator.Exceptions
                .Select(kv => new ExceptionHotspot(kv.Key, kv.Value.Count, kv.Value.Message, null))
                .OrderByDescending(e => e.Count)
                .Take(top)
                .ToList();
        }

        return model.Exceptions
            .GroupBy(e => e.Type, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var source = group
                    .SelectMany(e => e.Frames.Select(f => f.Source))
                    .FirstOrDefault(s => s is not null);
                return new ExceptionHotspot(group.Key, group.LongCount(), first.Message, source);
            })
            .OrderByDescending(e => e.Count)
            .Take(top)
            .ToList();
    }

    private static IReadOnlyList<TimelineBucket> BuildTimeline(
        Accumulator accumulator, TraceStackModel? model, double durationMs)
    {
        if (durationMs <= 0)
            return [];

        var bucketCount = Math.Clamp((int)Math.Ceiling(durationMs / 1000.0), 1, 120);
        var bucketMs = durationMs / bucketCount;

        var cpu = new double[bucketCount];
        var alloc = new long[bucketCount];
        var gen0 = new int[bucketCount];
        var gen1 = new int[bucketCount];
        var gen2 = new int[bucketCount];
        var pause = new double[bucketCount];
        var exceptions = new int[bucketCount];
        var contention = new double[bucketCount];

        int Bucket(double timeMs) => Math.Clamp((int)(timeMs / bucketMs), 0, bucketCount - 1);

        if (model is not null)
        {
            foreach (var sample in model.CpuSamples)
                cpu[Bucket(sample.TimeMs)] += sample.MetricMs;
            foreach (var allocation in model.Allocations)
                alloc[Bucket(allocation.TimeMs)] += allocation.Bytes;
            foreach (var exception in model.Exceptions)
                exceptions[Bucket(exception.TimeMs)]++;
        }

        foreach (var gcPause in accumulator.Pauses)
        {
            var index = Bucket(gcPause.StartMs);
            pause[index] += gcPause.DurationMs;
            switch (gcPause.Generation)
            {
                case 0: gen0[index]++; break;
                case 1: gen1[index]++; break;
                default: gen2[index]++; break;
            }
        }

        foreach (var (timeMs, waitMs) in accumulator.ContentionPoints)
            contention[Bucket(timeMs)] += waitMs;

        var result = new List<TimelineBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            result.Add(new TimelineBucket(
                Math.Round(i * bucketMs, 1),
                Math.Round(bucketMs, 1),
                Math.Round(cpu[i], 1),
                alloc[i],
                gen0[i], gen1[i], gen2[i],
                Math.Round(pause[i], 1),
                exceptions[i],
                Math.Round(contention[i], 1)));
        }

        return result;
    }

    private static GcSummary BuildGcSummary(Accumulator accumulator, double durationMs)
    {
        var totalPause = accumulator.Pauses.Sum(p => p.DurationMs);
        var maxPause = accumulator.Pauses.Count == 0 ? 0 : accumulator.Pauses.Max(p => p.DurationMs);
        var longest = accumulator.Pauses
            .OrderByDescending(p => p.DurationMs)
            .Take(10)
            .ToList();

        return new GcSummary(
            accumulator.Gen0,
            accumulator.Gen1,
            accumulator.Gen2,
            Math.Round(totalPause, 1),
            Math.Round(maxPause, 1),
            durationMs <= 0 ? 0 : Math.Round(totalPause * 100.0 / durationMs, 2),
            accumulator.HeapBytes,
            accumulator.LohBytes,
            accumulator.PromotedBytes,
            accumulator.SampledAllocBytes,
            accumulator.FinalizedObjects,
            longest);
    }

    private static string DisplayName(string? ns, string? name)
    {
        var methodName = name ?? "<unknown>";
        if (string.IsNullOrWhiteSpace(ns))
            return methodName;
        return methodName.StartsWith(ns + ".", StringComparison.Ordinal) ? methodName : $"{ns}.{methodName}";
    }

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private sealed record CpuResult(
        IReadOnlyList<CpuHotspot> Hotspots,
        IReadOnlyList<CpuStackHotspot> Stacks,
        IReadOnlyList<CpuLineHotspot> Lines,
        long Samples,
        double TotalMs);

    private sealed class Accumulator
    {
        public double FirstMs = double.MaxValue;
        public double LastMs = double.MinValue;
        public long EventCount;
        public int EventsLost;
        public long SampleEvents;
        public long AllocationTicks;
        public long ExceptionCount;

        public readonly Dictionary<int, (double Time, int Generation, string Reason, string Kind)> GcStarts = new();
        public readonly List<GcPauseInfo> Pauses = new();
        public int Gen0, Gen1, Gen2;
        public long HeapBytes, LohBytes, PromotedBytes, FinalizedObjects;
        public long SampledAllocBytes;
        public readonly Dictionary<string, (long Bytes, long Samples)> Allocs = new(StringComparer.Ordinal);

        public readonly Dictionary<string, (long Count, string? Message)> Exceptions = new(StringComparer.Ordinal);
        public long ContentionEvents;
        public double ContentionTotalMs, ContentionMaxMs;
        public readonly List<(double TimeMs, double WaitMs)> ContentionPoints = new();
        public readonly Dictionary<string, (long Waits, double Total, double Max)> ContentionKinds = new();
        public int MinActiveWorkers = int.MaxValue, MaxActiveWorkers;
        public long Enqueued, Dequeued, MaxBacklog;
        public int AdjustmentSamples;

        public readonly Dictionary<long, (double Time, int IlSize, string Name)> JitStarts = new();
        public readonly Dictionary<long, (double Ms, int IlSize, string Name)> JitTotals = new();
        public int JitMethods;
        public double TotalJitMs;

        public void ObserveWorkers(int active)
        {
            if (active < MinActiveWorkers) MinActiveWorkers = active;
            if (active > MaxActiveWorkers) MaxActiveWorkers = active;
        }

        public void CompleteJit(MethodLoadUnloadVerboseTraceData load)
        {
            if (!JitStarts.Remove(load.MethodID, out var start))
                return;

            var ms = Math.Max(0, load.TimeStampRelativeMSec - start.Time);
            JitMethods++;
            TotalJitMs += ms;

            var name = string.IsNullOrWhiteSpace(load.MethodName)
                ? start.Name
                : DisplayName(load.MethodNamespace, load.MethodName);
            var current = JitTotals.TryGetValue(load.MethodID, out var stats) ? stats : default;
            JitTotals[load.MethodID] = (current.Ms + ms, start.IlSize, name);
        }
    }
}
