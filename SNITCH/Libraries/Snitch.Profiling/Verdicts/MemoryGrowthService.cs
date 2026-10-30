using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;
using Snitch.Profiling.Snapshots;

namespace Snitch.Profiling.Verdicts;

public interface IMemoryGrowthService
{
    /// <summary>
    /// Answers "why is memory growing?" by comparing two heap snapshots written with
    /// <c>profile_heap writeJson=true</c>. A trace is optional; when it is a gc trace it supplies the
    /// allocation sites and the GC numbers, which is what turns "this type grew" into "this line
    /// allocates it".
    /// </summary>
    Task<MemoryGrowthReport> CompareAsync(
        string baselineJsonPath,
        string currentJsonPath,
        string? tracePath = null,
        int top = 25,
        bool resolveSource = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the heap of a live process now, writes it as JSON (so it can be a baseline later)
    /// and answers from it: against <paramref name="baselineJsonPath"/> when one is given, otherwise
    /// as a single snapshot that names the current owner but cannot prove growth.
    /// </summary>
    Task<MemoryGrowthReport> CaptureAsync(
        int processId,
        string processName,
        string? baselineJsonPath,
        string? tracePath,
        string outputDirectory,
        int top = 25,
        bool resolveSource = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The "why is memory growing" verdict. It does not walk the heap itself — it reuses the heap
/// snapshot, the heap comparison and (when a trace is supplied) the allocation-site analysis, then
/// commits to one statement: which type grew, by how much, how many more instances of it exist, and
/// whether the code allocates that type at a known line.
///
/// The correlation is the part a diff alone cannot give: "MyApp.Order grew by 120 MB" and "OrderService
/// allocates 45 MB of MyApp.Order at Orders.cs:88" are two reports; together they say the growth is
/// produced at that line rather than held by an old cache. When no baseline exists the answer says so
/// instead of pretending a single snapshot proves a trend.
/// </summary>
public sealed class MemoryGrowthService : IMemoryGrowthService
{
    private const long GrowthFloorBytes = 10L * 1024 * 1024;
    private const double GrowthFloorPercent = 20;

    private readonly IProfilingComparisonService _comparison;
    private readonly IManagedHeapService _heap;
    private readonly ITraceAnalysisService _analysis;
    private readonly IAllocationSiteService _allocations;
    private readonly IProfilingAdvisor _advisor;

    public MemoryGrowthService(
        IProfilingComparisonService comparison,
        IManagedHeapService heap,
        ITraceAnalysisService analysis,
        IAllocationSiteService allocations,
        IProfilingAdvisor advisor)
    {
        _comparison = comparison;
        _heap = heap;
        _analysis = analysis;
        _allocations = allocations;
        _advisor = advisor;
    }

    public async Task<MemoryGrowthReport> CompareAsync(
        string baselineJsonPath,
        string currentJsonPath,
        string? tracePath = null,
        int top = 25,
        bool resolveSource = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(baselineJsonPath))
            throw new FileNotFoundException($"Baseline heap snapshot not found: {baselineJsonPath}", baselineJsonPath);
        if (!File.Exists(currentJsonPath))
            throw new FileNotFoundException($"Current heap snapshot not found: {currentJsonPath}", currentJsonPath);

        var baseline = HeapSnapshotJson.Read(baselineJsonPath);
        var current = HeapSnapshotJson.Read(currentJsonPath);
        var comparison = await _comparison.CompareAsync(
            baselineJsonPath, currentJsonPath, "heap", top, cancellationToken).ConfigureAwait(false);
        var (analysis, allocations) = await ReadTraceAsync(tracePath, top, resolveSource, cancellationToken)
            .ConfigureAwait(false);

        return Build("compare", baselineJsonPath, currentJsonPath, baseline, current, comparison, analysis, allocations);
    }

    public async Task<MemoryGrowthReport> CaptureAsync(
        int processId,
        string processName,
        string? baselineJsonPath,
        string? tracePath,
        string outputDirectory,
        int top = 25,
        bool resolveSource = true,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));

        var current = await _heap.CaptureAsync(
            processId, top, focusType: null, includeDuplicateStrings: true, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(outputDirectory);
        var safeName = string.Concat(processName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var jsonPath = Path.Combine(
            outputDirectory, $"heap-{safeName}-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(jsonPath, HeapSnapshotJson.Write(current), cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(baselineJsonPath) && File.Exists(baselineJsonPath))
            return await CompareAsync(baselineJsonPath, jsonPath, tracePath, top, resolveSource, cancellationToken)
                .ConfigureAwait(false);

        var (analysis, allocations) = await ReadTraceAsync(tracePath, top, resolveSource, cancellationToken)
            .ConfigureAwait(false);

        return Build("single-snapshot", null, jsonPath, null, current, comparison: null, analysis, allocations);
    }

    private async Task<(TraceAnalysis? Analysis, AllocationSiteReport? Allocations)> ReadTraceAsync(
        string? tracePath, int top, bool resolveSource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tracePath) || !File.Exists(tracePath))
            return (null, null);

        var analysis = await _analysis.AnalyzeAsync(
            tracePath, top, resolveSource, deep: true, cancellationToken).ConfigureAwait(false);
        var allocations = await _allocations.AnalyzeAsync(
            tracePath, top, focusType: null, resolveSource, cancellationToken).ConfigureAwait(false);
        return (analysis, allocations);
    }

    private MemoryGrowthReport Build(
        string mode,
        string? baselinePath,
        string? currentPath,
        HeapSnapshot? baseline,
        HeapSnapshot current,
        ProfileComparison? comparison,
        TraceAnalysis? analysis,
        AllocationSiteReport? allocations)
    {
        var warnings = new List<string>();
        if (analysis is not null) warnings.AddRange(analysis.Warnings);
        if (allocations?.Truncated == true)
            warnings.Add("The allocation samples were capped; the site shares are a lower bound.");

        if (current.Truncated)
            warnings.Add($"The heap walk stopped at the safety limit ({current.ObjectCount:N0} objects read): the totals are a lower bound.");
        if (baseline?.Truncated == true)
            warnings.Add("The baseline heap walk was truncated: the delta is a lower bound.");

        var heapDelta = comparison is null
            ? null
            : comparison.Metrics.FirstOrDefault(m => m.Name == "Heap bytes");
        var grew = heapDelta is not null &&
                   heapDelta.Delta >= GrowthFloorBytes &&
                   heapDelta.Percent >= GrowthFloorPercent;
        var shrank = heapDelta is not null &&
                     heapDelta.Delta <= -GrowthFloorBytes &&
                     heapDelta.Percent <= -GrowthFloorPercent;

        var growing = comparison is null
            ? null
            : comparison.Heap
                .Where(d => d.DeltaBytes > 0)
                .OrderByDescending(d => d.DeltaBytes)
                .FirstOrDefault();
        var owner = current.TopTypes.FirstOrDefault();
        var ownerSite = owner is null || allocations is null
            ? null
            : allocations.TopSites.FirstOrDefault(s => s.TypeName == owner.TypeName);
        var growingSite = growing is null || allocations is null
            ? null
            : allocations.TopSites.FirstOrDefault(s => s.TypeName == growing.TypeName);

        string verdict;
        string answer;
        ProfilingFinding verdictFinding;

        if (mode == "single-snapshot")
        {
            verdict = "single-snapshot";
            (answer, verdictFinding) = SingleSnapshot(current, owner, ownerSite);
        }
        else if (grew)
        {
            verdict = "growth";
            (answer, verdictFinding) = Growing(heapDelta!, growing, growingSite, current);
        }
        else if (shrank)
        {
            verdict = "shrink";
            (answer, verdictFinding) = Shrinking(heapDelta!);
        }
        else
        {
            verdict = "stable";
            (answer, verdictFinding) = Stable(heapDelta, growing);
        }

        var verdictWeight = verdict switch
        {
            "growth" => Math.Min(1.0, (growing?.DeltaBytes ?? 0) / (double)Math.Max(1, current.TotalBytes)),
            "shrink" => 1.0,
            _ => 0.5,
        };

        var evidence = new List<AnswerEvidence>
        {
            new(1, Math.Round(verdictWeight, 4), verdictFinding.Id, verdictFinding.Severity,
                verdictFinding.Title, verdictFinding.Evidence, verdictFinding.Recommendation,
                verdictFinding.SuggestedAction, verdictFinding.Source),
        };

        var supporting = new List<WeightedFinding>();
        if (comparison is not null)
        {
            supporting.AddRange(comparison.Findings.Select(f => new WeightedFinding(
                f.Id switch
                {
                    "heap-growth" => 1.0,
                    "heap-type-growth" => verdictWeight,
                    _ => 0.2,
                },
                f)));
        }
        supporting.AddRange(_advisor.Analyze(current).Select(f => new WeightedFinding(
            f.Id switch
            {
                "heap-type-dominance" => (current.TopTypes.FirstOrDefault()?.Share ?? 0) / 100.0,
                "heap-loh" => current.TotalBytes > 0 ? current.LohBytes / (double)current.TotalBytes : 0,
                _ => 0.2,
            },
            f)));
        if (analysis is not null)
        {
            supporting.AddRange(_advisor.Analyze(analysis).Select(f => new WeightedFinding(
                f.Id is "gc-pause" or "gen2-pressure" or "loh-pressure" or "finalizer-pressure" or "allocation-rate"
                    ? Math.Max(0.2, analysis.Gc.PauseShare / 100.0)
                    : 0.05,
                f)));
        }
        if (allocations is not null)
        {
            supporting.AddRange(allocations.Findings.Select(f => new WeightedFinding(
                f.Id switch
                {
                    "allocation-site" => (growingSite ?? ownerSite) is { } site ? site.Share / 100.0 : 0.2,
                    "allocation-type" => (allocations.ByType.FirstOrDefault()?.Share ?? 0) / 100.0,
                    _ => 0.1,
                },
                f)));
        }

        evidence.AddRange(AnswerBuilder.Rank(
            supporting.Where(s => s.Weight > 0 && s.Finding.Id != verdictFinding.Id), startRank: 2));

        if (mode == "single-snapshot")
            warnings.Add("One snapshot cannot prove growth: compare it with a baseline taken before the workload.");

        var extraSteps = verdict switch
        {
            "single-snapshot" => new[]
            {
                "Take a second snapshot after the workload and compare the two: profile_heap writeJson=true, then this analysis with both paths.",
            },
            "growth" => new[]
            {
                "Re-measure after the fix with a third snapshot and compare it with the current one.",
            },
            "stable" => new[]
            {
                "If the OS still reports growth, take the second snapshot after an explicit GC.Collect() to separate uncollected garbage from retained memory.",
            },
            _ => Array.Empty<string>(),
        };

        return new MemoryGrowthReport(
            new AnswerReport(
                "Why is memory growing?",
                verdict,
                answer,
                Confidence(mode, baseline, analysis),
                evidence,
                AnswerBuilder.NextSteps(evidence, extraSteps),
                Measurements(mode, current, heapDelta, growing, analysis, allocations, owner),
                warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            mode,
            baselinePath,
            currentPath,
            current,
            comparison,
            allocations,
            analysis?.Gc);
    }

    private static (string Answer, ProfilingFinding Finding) SingleSnapshot(
        HeapSnapshot current, HeapTypeStat? owner, AllocationSiteHotspot? ownerSite)
    {
        var ownerText = owner is null
            ? ""
            : $", and {AnswerBuilder.ShortName(owner.TypeName)} owns {owner.Share:0.0}% of it " +
              $"({AnswerBuilder.Mb(owner.Bytes)} in {owner.Count:N0} instances)";
        var siteText = ownerSite is null
            ? ""
            : $"; that type is allocated most in {ownerSite.Site}" + (ownerSite.Source is null ? "" : $" ({ownerSite.Source})");

        var answer =
            $"The managed heap is {AnswerBuilder.Mb(current.TotalBytes)} right now{ownerText}{siteText}. " +
            "One snapshot names the owner but cannot prove growth: compare it with a baseline taken before the workload.";

        return (answer, new ProfilingFinding(
            "memory-single-snapshot", "info",
            "One heap snapshot: the owner is named, the trend is not",
            $"heap {AnswerBuilder.Mb(current.TotalBytes)} in {current.ObjectCount:N0} objects, " +
            $"LOH {AnswerBuilder.Mb(current.LohBytes)} ({Share(current.LohBytes, current.TotalBytes):0.0}%), " +
            $"{current.FinalizableObjects:N0} finalizable, {current.DuplicateStrings.Count} duplicated value(s).",
            "Take two snapshots separated by the workload; the delta is what identifies a leak.",
            "profile_heap writeJson=true before and after, then run this analysis with both paths.",
            ownerSite?.Source));
    }

    private static (string Answer, ProfilingFinding Finding) Growing(
        MetricDelta delta, TypeDelta? growing, AllocationSiteHotspot? site, HeapSnapshot current)
    {
        var typeText = growing is null
            ? "and no single type accounts for the growth"
            : $"through {AnswerBuilder.ShortName(growing.TypeName)}: {growing.LeftCount:N0} → {growing.RightCount:N0} instances " +
              $"(+{growing.DeltaBytes / 1024.0 / 1024.0:0.0} MB)";
        var siteText = site is null
            ? ""
            : $", and the code allocates that type at {site.Site}" + (site.Source is null ? "" : $" ({site.Source})") +
              $" — {site.SampledBytes / 1024.0 / 1024.0:0.0} MB sampled there";

        var answer =
            $"Memory grows {typeText}{siteText}: heap {AnswerBuilder.Mb((long)delta.Left)} → {AnswerBuilder.Mb((long)delta.Right)} " +
            $"({AnswerBuilder.Pct(delta.Percent)}, {AnswerBuilder.Mb(delta.Delta)}).";

        var recommendation = growing is null
            ? "The delta is spread over many types: take a dump of the current run and inspect roots, or narrow the window between snapshots."
            : site is null
                ? "The growth is in a type but no allocation site is known: collect a gc trace (profile='gc') and rerun so the site can be correlated."
                : $"The growth is produced at {site.Site}: bound the collection or stop retaining the instance there.";
        var next = site is null || site.Source is null
            ? "profile_allocations on a gc trace to find the allocating line."
            : $"Open {site.Source}, fix the retention/allocation, then compare a third snapshot.";

        return (answer, new ProfilingFinding(
            "memory-growth-driver", delta.Percent >= 50 ? "warning" : "info",
            growing is null
                ? "The heap grew, spread across types"
                : $"{AnswerBuilder.ShortName(growing.TypeName)} is the type the heap grew through",
            $"Heap {AnswerBuilder.Mb((long)delta.Left)} → {AnswerBuilder.Mb((long)delta.Right)} " +
            $"({AnswerBuilder.Mb(delta.Delta)}, {AnswerBuilder.Pct(delta.Percent)}); " +
            (growing is null
                ? "no single type grew."
                : $"{growing.TypeName}: {growing.LeftBytes / 1024.0 / 1024.0:0.0} MB → {growing.RightBytes / 1024.0 / 1024.0:0.0} MB " +
                  $"({growing.LeftCount:N0} → {growing.RightCount:N0} instances), " +
                  $"{Math.Min(100, growing.DeltaBytes * 100.0 / Math.Max(1, current.TotalBytes)):0.0}% of the current heap."),
            recommendation,
            next,
            site?.Source));
    }

    private static (string Answer, ProfilingFinding Finding) Shrinking(MetricDelta delta)
    {
        var answer =
            $"Memory went down between the two snapshots: {AnswerBuilder.Mb((long)delta.Left)} → " +
            $"{AnswerBuilder.Mb((long)delta.Right)} ({AnswerBuilder.Mb(delta.Delta)}, {AnswerBuilder.Pct(delta.Percent)}). " +
            "If a change was made, it released memory; if nothing changed, the baseline probably held uncollected garbage.";

        return (answer, new ProfilingFinding(
            "memory-shrink", "info",
            "The heap shrank between the two snapshots",
            $"Heap {AnswerBuilder.Mb((long)delta.Left)} → {AnswerBuilder.Mb((long)delta.Right)} " +
            $"({AnswerBuilder.Mb(delta.Delta)}, {AnswerBuilder.Pct(delta.Percent)}).",
            "Confirm with a third snapshot that the level is stable and not a one-off collection.",
            "Take a third snapshot after the same workload and compare it with the current one.",
            null));
    }

    private static (string Answer, ProfilingFinding Finding) Stable(MetricDelta? delta, TypeDelta? growing)
    {
        var answer = delta is null
            ? "No usable heap delta: the comparison produced no heap totals, so there is no growth to judge."
            : $"Memory is not growing between the two snapshots: {AnswerBuilder.Mb((long)delta.Left)} → " +
              $"{AnswerBuilder.Mb((long)delta.Right)} ({AnswerBuilder.Pct(delta.Percent)}), below the " +
              "10 MB / 20 % floor this analysis uses, and no type moved by more than that.";

        return (answer, new ProfilingFinding(
            "memory-stable", "info",
            "The heap is stable between the two snapshots",
            delta is null
                ? "No heap metric was reported."
                : $"Heap {AnswerBuilder.Mb((long)delta.Left)} → {AnswerBuilder.Mb((long)delta.Right)} " +
                  $"({AnswerBuilder.Mb(delta.Delta)}, {AnswerBuilder.Pct(delta.Percent)})" +
                  (growing is null ? "." : $"; largest positive type delta {AnswerBuilder.ShortName(growing.TypeName)} " +
                                             $"+{growing.DeltaBytes / 1024.0 / 1024.0:0.0} MB."),
            "No leak signal in this window. If the process still grows, the memory may be unmanaged or held by a collection that has not been collected yet.",
            "Take the second snapshot after an explicit GC.Collect() to separate garbage from retained memory.",
            null));
    }

    private static string Confidence(string mode, HeapSnapshot? baseline, TraceAnalysis? analysis)
    {
        if (mode == "single-snapshot")
            return "low";
        var truncated = baseline?.Truncated ?? false;
        if (analysis is not null && !truncated)
            return "high";
        return truncated ? "low" : "medium";
    }

    private static IReadOnlyList<string> Measurements(
        string mode, HeapSnapshot current, MetricDelta? heapDelta, TypeDelta? growing,
        TraceAnalysis? analysis, AllocationSiteReport? allocations, HeapTypeStat? owner)
    {
        var lines = new List<string>
        {
            $"heap: {AnswerBuilder.Mb(current.TotalBytes)} in {current.ObjectCount:N0} objects, " +
            $"LOH {AnswerBuilder.Mb(current.LohBytes)} ({Share(current.LohBytes, current.TotalBytes):0.0}%), " +
            $"pinned {AnswerBuilder.Mb(current.PinnedBytes)}",
            $"finalizable: {current.FinalizableObjects:N0}; duplicated strings: " +
            $"{AnswerBuilder.Mb(current.DuplicateStrings.Sum(d => d.WastedBytes))} wasted",
        };

        if (mode == "single-snapshot" && owner is not null)
            lines.Add($"owner: {owner.TypeName} {owner.Share:0.0}% ({AnswerBuilder.Mb(owner.Bytes)}, {owner.Count:N0} instances)");
        if (heapDelta is not null)
            lines.Add($"heap delta: {AnswerBuilder.Mb((long)heapDelta.Left)} → {AnswerBuilder.Mb((long)heapDelta.Right)} " +
                      $"({AnswerBuilder.Mb(heapDelta.Delta)}, {AnswerBuilder.Pct(heapDelta.Percent)})");
        if (growing is not null)
            lines.Add($"largest type delta: {growing.TypeName} +{growing.DeltaBytes / 1024.0 / 1024.0:0.0} MB, " +
                      $"{growing.LeftCount:N0} → {growing.RightCount:N0} instances");
        if (analysis is not null)
            lines.Add($"gc: {analysis.Gc.Gen0 + analysis.Gc.Gen1 + analysis.Gc.Gen2} collections, {analysis.Gc.Gen2} gen-2, " +
                      $"{AnswerBuilder.Mb(analysis.Gc.PromotedBytes)} promoted, {analysis.Gc.PauseShare:0.0}% paused");
        if (allocations is not null && allocations.TopSites.FirstOrDefault() is { } topSite)
            lines.Add($"top allocation site: {topSite.Site} allocates {topSite.TypeName} " +
                      $"{topSite.SampledBytes / 1024.0 / 1024.0:0.0} MB ({topSite.Share:0.0}%)" +
                      (topSite.Source is null ? "" : $" at {topSite.Source}"));
        return lines;
    }

    private static double Share(long part, long total) => total <= 0 ? 0 : part * 100.0 / total;
}
