using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IProfilingComparisonService
{
    Task<ProfileComparison> CompareAsync(
        string leftPath, string rightPath, string kind, int top,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Regression analysis between two measurements: two traces (CPU, allocations, GC, exceptions) or
/// two heap snapshots saved as JSON. This is the loop that turns profiling into a change-verification
/// tool — collect a baseline, change the code, collect again, and look at the deltas and their
/// rule-based findings instead of re-reading two reports by hand.
/// </summary>
public sealed class ProfilingComparisonService : IProfilingComparisonService
{
    private readonly ITraceAnalysisService _analysis;
    private readonly IProfilingAdvisor _advisor;

    public ProfilingComparisonService(ITraceAnalysisService analysis, IProfilingAdvisor advisor)
    {
        _analysis = analysis;
        _advisor = advisor;
    }

    public Task<ProfileComparison> CompareAsync(
        string leftPath, string rightPath, string kind, int top,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(leftPath)) throw new FileNotFoundException($"Left file not found: {leftPath}", leftPath);
        if (!File.Exists(rightPath)) throw new FileNotFoundException($"Right file not found: {rightPath}", rightPath);

        top = Math.Clamp(top <= 0 ? 25 : top, 1, 200);
        var effectiveKind = ResolveKind(kind, leftPath, rightPath);

        return effectiveKind switch
        {
            "trace" => CompareTracesAsync(leftPath, rightPath, top, cancellationToken),
            "heap" => Task.Run(() => CompareHeaps(leftPath, rightPath, top), cancellationToken),
            _ => throw new ArgumentException(
                $"Unknown comparison kind '{kind}'. Use auto, trace or heap (heap needs two .json snapshots written by profile_heap writeJson=true)."),
        };
    }

    private static string ResolveKind(string kind, string left, string right)
    {
        if (!string.Equals(kind, "auto", StringComparison.OrdinalIgnoreCase))
            return kind.Trim().ToLowerInvariant();

        static bool IsTrace(string path) =>
            path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase);

        if (IsTrace(left) && IsTrace(right))
            return "trace";

        if (left.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            right.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return "heap";

        throw new ArgumentException(
            "Could not infer the comparison kind from the file extensions. Pass kind='trace' for two .nettrace files " +
            "or kind='heap' for two heap .json snapshots.");
    }

    private async Task<ProfileComparison> CompareTracesAsync(
        string leftPath, string rightPath, int top, CancellationToken cancellationToken)
    {
        // Ask for a wide hotspot list from each side so a method that was outside the top N on one
        // side is still compared; the caller's `top` only limits the returned deltas.
        var left = await _analysis.AnalyzeAsync(leftPath, 200, resolveSource: false, deep: true, cancellationToken)
            .ConfigureAwait(false);
        var right = await _analysis.AnalyzeAsync(rightPath, 200, resolveSource: false, deep: true, cancellationToken)
            .ConfigureAwait(false);

        var metrics = new List<MetricDelta>
        {
            // Duration first: a CPU total is only comparable with another total over the same
            // window, and the change review normalises per second from this row.
            Metric("Duration ms", left.Trace.DurationMs, right.Trace.DurationMs),
            Metric("CPU ms", left.Trace.TotalCpuMs, right.Trace.TotalCpuMs),
            Metric("CPU samples", left.Trace.CpuSamples, right.Trace.CpuSamples),
            Metric("Gen0", left.Gc.Gen0, right.Gc.Gen0),
            Metric("Gen1", left.Gc.Gen1, right.Gc.Gen1),
            Metric("Gen2", left.Gc.Gen2, right.Gc.Gen2),
            Metric("GC pause ms", left.Gc.TotalPauseMs, right.Gc.TotalPauseMs),
            Metric("Heap bytes", left.Gc.HeapBytes, right.Gc.HeapBytes),
            Metric("Sampled allocation bytes", left.Gc.SampledAllocationBytes, right.Gc.SampledAllocationBytes),
            Metric("Exceptions", left.Exceptions.Sum(e => e.Count), right.Exceptions.Sum(e => e.Count)),
            Metric("Contention ms", left.Contention.TotalWaitMs, right.Contention.TotalWaitMs),
            Metric("Max pool backlog", left.ThreadPool.MaxBacklog, right.ThreadPool.MaxBacklog),
        };

        var cpuDeltas = new Dictionary<string, CpuMethodDelta>(StringComparer.Ordinal);
        AddCpu(cpuDeltas, left.CpuHotspots, right.CpuHotspots);
        AddCpu(cpuDeltas, right.CpuHotspots, left.CpuHotspots, reverse: true);

        var allocationDeltas = new Dictionary<string, TypeDelta>(StringComparer.Ordinal);
        AddAllocations(allocationDeltas, left.Allocations, right.Allocations);
        AddAllocations(allocationDeltas, right.Allocations, left.Allocations, reverse: true);

        var cpu = cpuDeltas.Values
            .OrderByDescending(d => Math.Abs(d.DeltaMs))
            .Take(top)
            .ToList();
        var allocations = allocationDeltas.Values
            .OrderByDescending(d => Math.Abs(d.DeltaBytes))
            .Take(top)
            .ToList();

        var findings = BuildTraceFindings(metrics, cpu, allocations);
        return new ProfileComparison(
            "trace", Path.GetFullPath(leftPath), Path.GetFullPath(rightPath),
            metrics, cpu, allocations, [], findings, _advisor.NextSteps(findings));
    }

    private static void AddCpu(
        Dictionary<string, CpuMethodDelta> target,
        IReadOnlyList<CpuHotspot> current,
        IReadOnlyList<CpuHotspot> other,
        bool reverse = false)
    {
        foreach (var hotspot in current)
        {
            if (target.ContainsKey(hotspot.Method))
                continue;

            var leftMs = reverse ? (other.FirstOrDefault(h => h.Method == hotspot.Method)?.EstimatedCpuMs ?? 0) : hotspot.EstimatedCpuMs;
            var rightMs = reverse ? hotspot.EstimatedCpuMs : (other.FirstOrDefault(h => h.Method == hotspot.Method)?.EstimatedCpuMs ?? 0);
            var delta = rightMs - leftMs;

            target[hotspot.Method] = new CpuMethodDelta(
                hotspot.Method, hotspot.Source, leftMs, rightMs, delta, Percent(leftMs, rightMs));
        }
    }

    private static void AddAllocations(
        Dictionary<string, TypeDelta> target,
        IReadOnlyList<AllocationHotspot> current,
        IReadOnlyList<AllocationHotspot> other,
        bool reverse = false)
    {
        foreach (var allocation in current)
        {
            if (target.ContainsKey(allocation.TypeName))
                continue;

            var otherStats = other.FirstOrDefault(a => a.TypeName == allocation.TypeName);
            var leftBytes = reverse ? (otherStats?.SampledBytes ?? 0) : allocation.SampledBytes;
            var rightBytes = reverse ? allocation.SampledBytes : (otherStats?.SampledBytes ?? 0);
            var leftCount = reverse ? (otherStats?.Samples ?? 0) : allocation.Samples;
            var rightCount = reverse ? allocation.Samples : (otherStats?.Samples ?? 0);

            target[allocation.TypeName] = new TypeDelta(
                allocation.TypeName, leftBytes, rightBytes, rightBytes - leftBytes,
                leftCount, rightCount, rightCount - leftCount, Percent(leftBytes, rightBytes));
        }
    }

    private static IReadOnlyList<ProfilingFinding> BuildTraceFindings(
        IReadOnlyList<MetricDelta> metrics,
        IReadOnlyList<CpuMethodDelta> cpu,
        IReadOnlyList<TypeDelta> allocations)
    {
        var findings = new List<ProfilingFinding>();

        var total = metrics.First(m => m.Name == "CPU ms");
        if (total.Delta >= 100 && total.Percent >= 20)
            findings.Add(new ProfilingFinding(
                "cpu-total-regression", "warning",
                "Total CPU increased",
                $"{total.Left:N0} ms → {total.Right:N0} ms (+{total.Delta:N0} ms, +{total.Percent:0.0}%).",
                "Find the method delta below that explains the increase; a single entry usually accounts for most of it.",
                null,
                null));
        else if (total.Delta <= -100 && total.Percent <= -20)
            findings.Add(new ProfilingFinding(
                "cpu-total-improvement", "info",
                "Total CPU decreased",
                $"{total.Left:N0} ms → {total.Right:N0} ms ({total.Delta:N0} ms, {total.Percent:0.0}%).",
                "Keep the change; use the per-method deltas to confirm the intended method moved.",
                null,
                null));

        var cpuRegression = cpu.FirstOrDefault(d => d.DeltaMs >= 100 && d.Percent >= 20);
        if (cpuRegression is not null)
            findings.Add(new ProfilingFinding(
                "cpu-regression", "warning",
                $"CPU increased in {ShortName(cpuRegression.Method)}",
                $"{cpuRegression.Method}: {cpuRegression.LeftMs:N0} ms → {cpuRegression.RightMs:N0} ms " +
                $"(+{cpuRegression.DeltaMs:N0} ms, +{cpuRegression.Percent:0.0}%).",
                "This is the method to inspect: compare its source between the two revisions or benchmark it directly.",
                "read_symbol " + ShortName(cpuRegression.Method),
                cpuRegression.Source));

        var allocationRegression = allocations.FirstOrDefault(d => d.DeltaBytes >= 10 * 1024 * 1024 && d.Percent >= 25);
        if (allocationRegression is not null)
            findings.Add(new ProfilingFinding(
                "allocation-regression", "warning",
                $"Allocations increased for {ShortName(allocationRegression.TypeName)}",
                $"{allocationRegression.TypeName}: {allocationRegression.LeftBytes / 1024.0 / 1024.0:N1} MB → " +
                $"{allocationRegression.RightBytes / 1024.0 / 1024.0:N1} MB (+{allocationRegression.DeltaBytes / 1024.0 / 1024.0:N1} MB).",
                "Run profile_allocations on the right trace to see the site that started allocating it.",
                null,
                null));

        var gen2 = metrics.First(m => m.Name == "Gen2");
        if (gen2.Delta >= 3)
            findings.Add(new ProfilingFinding(
                "gc-regression", "warning",
                "More generation-2 collections",
                $"{gen2.Left:0} → {gen2.Right:0} gen-2 collections (+{gen2.Delta:0}).",
                "Objects are surviving longer; take a heap snapshot and compare the type deltas.",
                "profile_heap on both runs, then profile_compare kind='heap'.",
                null));

        var exceptions = metrics.First(m => m.Name == "Exceptions");
        if (exceptions.Delta >= 100)
            findings.Add(new ProfilingFinding(
                "exception-regression", "warning",
                "More exceptions are thrown",
                $"{exceptions.Left:N0} → {exceptions.Right:N0} exceptions (+{exceptions.Delta:N0}).",
                "Check the throw site: a validation path may have become a hot path.",
                "profile_analyze on the right trace (exceptions section).",
                null));

        return findings.OrderByDescending(f => f.Severity == "warning" ? 1 : 0).ToList();
    }

    private static ProfileComparison CompareHeaps(string leftPath, string rightPath, int top)
    {
        var left = ReadHeap(leftPath);
        var right = ReadHeap(rightPath);

        var metrics = new List<MetricDelta>
        {
            Metric("Heap bytes", left.TotalBytes, right.TotalBytes),
            Metric("Objects", left.ObjectCount, right.ObjectCount),
            Metric("LOH bytes", left.LohBytes, right.LohBytes),
            Metric("Pinned bytes", left.PinnedBytes, right.PinnedBytes),
            Metric("Finalizable objects", left.FinalizableObjects, right.FinalizableObjects),
            Metric("Duplicate string bytes", left.DuplicateStrings.Sum(d => d.WastedBytes),
                right.DuplicateStrings.Sum(d => d.WastedBytes)),
        };

        var deltas = new Dictionary<string, TypeDelta>(StringComparer.Ordinal);
        AddHeapTypes(deltas, left.TopTypes, right.TopTypes);
        AddHeapTypes(deltas, right.TopTypes, left.TopTypes, reverse: true);

        var types = deltas.Values
            .OrderByDescending(d => Math.Abs(d.DeltaBytes))
            .Take(top)
            .ToList();

        var findings = new List<ProfilingFinding>();
        var total = metrics.First(m => m.Name == "Heap bytes");
        if (total.Delta >= 10 * 1024 * 1024 && total.Percent >= 20)
            findings.Add(new ProfilingFinding(
                "heap-growth", "warning",
                "The managed heap grew",
                $"{total.Left / 1024.0 / 1024.0:N1} MB → {total.Right / 1024.0 / 1024.0:N1} MB " +
                $"(+{total.Delta / 1024.0 / 1024.0:N1} MB, +{total.Percent:0.0}%).",
                "Compare the type deltas below; the type that grew is the leak or the cache that lost its bound.",
                null,
                null));

        var typeGrowth = types.FirstOrDefault(d => d.DeltaBytes >= 10 * 1024 * 1024 && d.Percent >= 25);
        if (typeGrowth is not null)
            findings.Add(new ProfilingFinding(
                "heap-type-growth", "warning",
                $"{ShortName(typeGrowth.TypeName)} grew",
                $"{typeGrowth.LeftCount:N0} → {typeGrowth.RightCount:N0} instances, " +
                $"{typeGrowth.LeftBytes / 1024.0 / 1024.0:N1} MB → {typeGrowth.RightBytes / 1024.0 / 1024.0:N1} MB " +
                $"(+{typeGrowth.DeltaBytes / 1024.0 / 1024.0:N1} MB).",
                "Take a dump of the right run and inspect roots for this type if the count keeps growing.",
                "profile_heap focusType='" + typeGrowth.TypeName + "' on the right run.",
                null));

        var finalizable = metrics.First(m => m.Name == "Finalizable objects");
        if (finalizable.Delta >= 10_000)
            findings.Add(new ProfilingFinding(
                "finalizer-growth", "info",
                "More finalizable objects",
                $"{finalizable.Left:N0} → {finalizable.Right:N0} finalizable objects (+{finalizable.Delta:N0}).",
                "Check that the new objects implement IDisposable and are disposed deterministically.",
                null,
                null));

        var duplicate = metrics.First(m => m.Name == "Duplicate string bytes");
        if (duplicate.Delta >= 20L * 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "duplicate-strings-growth", "info",
                "Duplicated strings grew",
                $"Wasted bytes {duplicate.Left / 1024.0 / 1024.0:N1} MB → {duplicate.Right / 1024.0 / 1024.0:N1} MB.",
                "Intern or share the repeated values; the heap snapshot's duplicate-strings section names them.",
                null,
                null));

        return new ProfileComparison(
            "heap", Path.GetFullPath(leftPath), Path.GetFullPath(rightPath),
            metrics, [], [], types, findings, []);
    }

    private static void AddHeapTypes(
        Dictionary<string, TypeDelta> target,
        IReadOnlyList<HeapTypeStat> current,
        IReadOnlyList<HeapTypeStat> other,
        bool reverse = false)
    {
        foreach (var type in current)
        {
            if (target.ContainsKey(type.TypeName))
                continue;

            var otherStats = other.FirstOrDefault(t => t.TypeName == type.TypeName);
            var leftBytes = reverse ? (otherStats?.Bytes ?? 0) : type.Bytes;
            var rightBytes = reverse ? type.Bytes : (otherStats?.Bytes ?? 0);
            var leftCount = reverse ? (otherStats?.Count ?? 0) : type.Count;
            var rightCount = reverse ? type.Count : (otherStats?.Count ?? 0);

            target[type.TypeName] = new TypeDelta(
                type.TypeName, leftBytes, rightBytes, rightBytes - leftBytes,
                leftCount, rightCount, rightCount - leftCount, Percent(leftBytes, rightBytes));
        }
    }

    private static HeapSnapshot ReadHeap(string path) => HeapSnapshotJson.Read(path);

    private static MetricDelta Metric(string name, double left, double right) =>
        new(name, left, right, right - left, Percent(left, right));

    private static double Percent(double left, double right)
    {
        if (Math.Abs(left) < 0.0001)
            return Math.Abs(right) < 0.0001 ? 0 : 100;
        return Math.Round((right - left) * 100.0 / left, 2);
    }

    private static string ShortName(string fullName) => FrameNames.ShortName(fullName);
}
