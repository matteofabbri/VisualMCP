using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;

namespace Snitch.Profiling.Verdicts;

public interface IChangeReviewService
{
    /// <summary>
    /// Answers "what changed since the last measurement?" from two traces or two heap snapshots:
    /// a verdict (regression / improvement / unchanged / inconclusive), the normalized deltas it was
    /// based on, and the method or type that drove the verdict.
    /// </summary>
    Task<ChangeReviewReport> ReviewAsync(
        string baselinePath,
        string currentPath,
        string kind = "auto",
        int top = 25,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The "is this change a regression?" verdict. <see cref="IProfilingComparisonService"/> already
/// produces the deltas; this layer adds the three things a delta table cannot give:
///
/// 1. a unit that survives different collection windows — CPU ms per second of trace, allocation
///    MB per second, exceptions per second — because comparing raw CPU totals of a 3 s and a 30 s
///    trace says nothing;
/// 2. a significance floor per metric, so a 2 % wobble is reported as "unchanged" instead of as a
///    regression;
/// 3. a refusal: when the two windows are too short to compare, the answer is "inconclusive" with
///    the reason, not a number.
/// </summary>
public sealed class ChangeReviewService : IChangeReviewService
{
    /// <summary>Shorter than this and the per-second rates are dominated by startup and sampling noise.</summary>
    private const double MinComparableDurationMs = 500;

    /// <summary>Fewer CPU samples than this on either side and the CPU delta is not a measurement.</summary>
    private const long MinCpuSamples = 100;

    private static readonly Dictionary<string, double> Floors = new(StringComparer.Ordinal)
    {
        ["CPU"] = 50,                    // ms of CPU per second of trace
        ["Sampled allocations"] = 5,     // MB per second
        ["Exceptions"] = 5,              // per second
        ["GC pause"] = 20,               // ms per second
        ["Contention wait"] = 50,        // ms per second
        ["Gen2 collections"] = 1,        // per second
        ["Max pool backlog"] = 100,      // absolute items
    };

    private readonly IProfilingComparisonService _comparison;
    private readonly IProfilingAdvisor _advisor;

    public ChangeReviewService(IProfilingComparisonService comparison, IProfilingAdvisor advisor)
    {
        _comparison = comparison;
        _advisor = advisor;
    }

    public async Task<ChangeReviewReport> ReviewAsync(
        string baselinePath,
        string currentPath,
        string kind = "auto",
        int top = 25,
        CancellationToken cancellationToken = default)
    {
        var comparison = await _comparison.CompareAsync(baselinePath, currentPath, kind, top, cancellationToken)
            .ConfigureAwait(false);

        return comparison.Kind == "heap" ? ReviewHeap(comparison) : ReviewTrace(comparison);
    }

    private ChangeReviewReport ReviewTrace(ProfileComparison comparison)
    {
        var notes = new List<string>();
        var duration = comparison.Metrics.FirstOrDefault(m => m.Name == "Duration ms");
        var leftDuration = duration?.Left ?? 0;
        var rightDuration = duration?.Right ?? 0;
        var samples = comparison.Metrics.FirstOrDefault(m => m.Name == "CPU samples");
        var weakCpu = samples is not null && samples.Left < MinCpuSamples && samples.Right < MinCpuSamples;

        // Both refusal conditions are part of the verdict, not just of the CPU row: the docs promise
        // "inconclusive below 500 ms or below 100 CPU samples", so a run that fails either one cannot
        // produce a regression from the remaining metrics.
        var comparable = leftDuration >= MinComparableDurationMs &&
                         rightDuration >= MinComparableDurationMs &&
                         !weakCpu;
        if (weakCpu)
            notes.Add($"Only {samples!.Left:N0} and {samples.Right:N0} CPU samples were taken: below {MinCpuSamples:N0} " +
                      "the CPU delta is not a measurement, so the runs are not compared.");
        if (leftDuration < MinComparableDurationMs || rightDuration < MinComparableDurationMs)
            notes.Add($"The windows are {leftDuration:N0} ms (baseline) and {rightDuration:N0} ms (current); " +
                      $"below {MinComparableDurationMs:N0} ms a rate is dominated by collection overhead.");

        double PerSecond(double value, double windowMs) => windowMs > 0 ? value / (windowMs / 1000.0) : value;
        double Metric(string name, bool right) =>
            comparison.Metrics.FirstOrDefault(m => m.Name == name) is { } m ? (right ? m.Right : m.Left) : 0;

        var normalized = new List<NormalizedMetric>
        {
            Normalize("CPU", "ms/s",
                PerSecond(Metric("CPU ms", right: false), leftDuration),
                PerSecond(Metric("CPU ms", right: true), rightDuration),
                comparable),
            Normalize("Sampled allocations", "MB/s",
                PerSecond(Metric("Sampled allocation bytes", right: false), leftDuration) / 1024.0 / 1024.0,
                PerSecond(Metric("Sampled allocation bytes", right: true), rightDuration) / 1024.0 / 1024.0,
                comparable),
            Normalize("GC pause", "ms/s",
                PerSecond(Metric("GC pause ms", right: false), leftDuration),
                PerSecond(Metric("GC pause ms", right: true), rightDuration),
                comparable),
            Normalize("Contention wait", "ms/s",
                PerSecond(Metric("Contention ms", right: false), leftDuration),
                PerSecond(Metric("Contention ms", right: true), rightDuration),
                comparable),
            Normalize("Exceptions", "/s",
                PerSecond(Metric("Exceptions", right: false), leftDuration),
                PerSecond(Metric("Exceptions", right: true), rightDuration),
                comparable),
            Normalize("Gen2 collections", "/s",
                PerSecond(Metric("Gen2", right: false), leftDuration),
                PerSecond(Metric("Gen2", right: true), rightDuration),
                comparable),
            Normalize("Max pool backlog", "",
                Metric("Max pool backlog", right: false),
                Metric("Max pool backlog", right: true),
                comparable),
        };

        var candidates = normalized
            .Where(m => m.Comparable)
            .Select(m => new { Metric = m, Score = Math.Abs(m.Delta) / Math.Max(0.0001, Floor(m.Name)) })
            .OrderByDescending(x => x.Score)
            .ToList();
        var best = candidates.FirstOrDefault();

        var significant = best is not null &&
                          best.Score >= 1 && Math.Abs(best.Metric.Percent) >= 20;

        var verdict = !comparable
            ? "inconclusive"
            : significant
                ? best!.Metric.Delta > 0 ? "regression" : "improvement"
                : "unchanged";

        var cpuDriver = comparison.Cpu
            .Where(d => d.DeltaMs >= 100 && d.Percent >= 20)
            .OrderByDescending(d => d.DeltaMs)
            .FirstOrDefault();
        var allocationDriver = comparison.Allocations
            .Where(d => d.DeltaBytes > 0)
            .OrderByDescending(d => d.DeltaBytes)
            .FirstOrDefault();

        string answer;
        ProfilingFinding verdictFinding;

        switch (verdict)
        {
            case "regression":
            {
                var m = best!.Metric;
                var driverText = m.Name == "CPU" && cpuDriver is not null
                    ? $"; the extra CPU is in {AnswerBuilder.ShortName(cpuDriver.Method)} ({AnswerBuilder.Pct(cpuDriver.Percent)}"
                      + (cpuDriver.Source is null ? ")" : $", {cpuDriver.Source})")
                    : m.Name == "Sampled allocations" && allocationDriver is not null
                        ? $"; {AnswerBuilder.ShortName(allocationDriver.TypeName)} allocates {AnswerBuilder.Mb(allocationDriver.DeltaBytes)} more"
                        : "";
                answer =
                    $"Regression: {m.Name} rose from {m.Baseline:N1} to {m.Current:N1} {m.Unit} ({AnswerBuilder.Pct(m.Percent)}), " +
                    $"the largest significant move among {candidates.Count} comparable metrics{driverText}.";
                verdictFinding = new ProfilingFinding(
                    "change-regression", "warning", $"A regression in {m.Name}",
                    $"{m.Name}: {m.Baseline:N1} → {m.Current:N1} {m.Unit} ({AnswerBuilder.Pct(m.Percent)}), " +
                    $"floor {Floor(m.Name):N1} {m.Unit}.",
                    cpuDriver?.Source is null
                        ? "Inspect the change between the two revisions before tuning anything else."
                        : $"Inspect the change in {cpuDriver.Method} between the two revisions.",
                    cpuDriver?.Source is null ? "git_diff between the two revisions." : $"git_diff {cpuDriver.Source}",
                    cpuDriver?.Source);
                break;
            }

            case "improvement":
            {
                var m = best!.Metric;
                answer =
                    $"Improvement: {m.Name} fell from {m.Baseline:N1} to {m.Current:N1} {m.Unit} ({AnswerBuilder.Pct(m.Percent)}), " +
                    "the largest significant move; keep the change and confirm the intended method moved.";
                verdictFinding = new ProfilingFinding(
                    "change-improvement", "info", $"An improvement in {m.Name}",
                    $"{m.Name}: {m.Baseline:N1} → {m.Current:N1} {m.Unit} ({AnswerBuilder.Pct(m.Percent)}).",
                    "Keep the change; use the per-method deltas to confirm the method you changed is the one that moved.",
                    null,
                    null);
                break;
            }

            case "inconclusive":
            {
                var reason = weakCpu
                    ? $"only {samples!.Left:N0} and {samples.Right:N0} CPU samples were taken"
                    : !comparable
                        ? $"the windows are {leftDuration:N0} ms and {rightDuration:N0} ms"
                        : "no metric is comparable";
                answer =
                    "Not comparable: " + reason +
                    $", so a delta would be measurement noise rather than a change in the code (minimum {MinComparableDurationMs:N0} ms and {MinCpuSamples} CPU samples).";
                verdictFinding = new ProfilingFinding(
                    "change-inconclusive", "info", "The two runs are not comparable",
                    string.Join(" ", notes),
                    "Collect both traces with the same profile for at least 2 seconds under comparable load.",
                    "profile_collect duration=5 with the same profile on both runs, then run this review again.",
                    null);
                break;
            }

            default:
            {
                var m = best?.Metric;
                answer = m is null
                    ? "No comparable metric moved: the two runs produced no rates to compare."
                    : $"No meaningful change: the largest move was {m.Name} {m.Baseline:N1} → {m.Current:N1} {m.Unit} " +
                      $"({AnswerBuilder.Pct(m.Percent)}), under the {Floor(m.Name):N1} {m.Unit} floor this review uses.";
                verdictFinding = new ProfilingFinding(
                    "change-unchanged", "info", "No significant change between the two runs",
                    m is null
                        ? "No comparable metric."
                        : $"{m.Name}: {m.Baseline:N1} → {m.Current:N1} {m.Unit} ({AnswerBuilder.Pct(m.Percent)}); " +
                          $"floor {Floor(m.Name):N1} {m.Unit}.",
                    "No action needed. If a difference was expected, the window may be too short or the metric too coarse.",
                    null,
                    null);
                break;
            }
        }

        var evidence = new List<AnswerEvidence>
        {
            new(1, Math.Round(best is null ? 0 : Math.Min(2.0, best.Score) / 2.0, 4), verdictFinding.Id, verdictFinding.Severity,
                verdictFinding.Title, verdictFinding.Evidence, verdictFinding.Recommendation,
                verdictFinding.SuggestedAction, verdictFinding.Source),
        };
        evidence.AddRange(AnswerBuilder.Rank(
            comparison.Findings.Select(f => new WeightedFinding(ComparisonWeight(f.Id), f))
                .Where(s => s.Weight > 0 && s.Finding.Id != verdictFinding.Id),
            startRank: 2));

        var reviewSteps = ImprovementOrRegressionSteps(verdict)
            .Concat(comparison.Findings.Count > 0
                ? _advisor.NextSteps(comparison.Findings)
                : Array.Empty<string>())
            .ToList();

        return new ChangeReviewReport(
            new AnswerReport(
                "What changed since the last measurement?",
                verdict,
                answer,
                comparable ? "high" : "low",
                evidence,
                AnswerBuilder.NextSteps(evidence, reviewSteps),
                Measurements(normalized, comparison),
                notes),
            "trace",
            comparison.LeftPath,
            comparison.RightPath,
            comparable,
            normalized,
            notes,
            comparison);
    }

    private ChangeReviewReport ReviewHeap(ProfileComparison comparison)
    {
        var notes = new List<string>();
        var total = comparison.Metrics.FirstOrDefault(m => m.Name == "Heap bytes");
        var grew = total is not null && total.Delta >= 10 * 1024 * 1024 && total.Percent >= 20;
        var shrank = total is not null && total.Delta <= -10 * 1024 * 1024 && total.Percent <= -20;
        var verdict = grew ? "regression" : shrank ? "improvement" : "unchanged";

        var driver = comparison.Heap
            .Where(d => d.DeltaBytes > 0)
            .OrderByDescending(d => d.DeltaBytes)
            .FirstOrDefault();

        var normalized = comparison.Metrics
            .Select(m => new NormalizedMetric(
                m.Name,
                m.Name.EndsWith("bytes", StringComparison.OrdinalIgnoreCase) ? "bytes" : "count",
                Math.Round(m.Left, 2), Math.Round(m.Right, 2), Math.Round(m.Delta, 2), m.Percent, true))
            .ToList();

        string answer;
        ProfilingFinding verdictFinding;
        if (verdict == "regression" && total is not null)
        {
            answer =
                $"Regression: the managed heap grew from {AnswerBuilder.Mb(total.Left)} to {AnswerBuilder.Mb(total.Right)} " +
                $"({AnswerBuilder.Pct(total.Percent)}, {AnswerBuilder.Mb(total.Delta)})" +
                (driver is null
                    ? ", spread across types."
                    : $", driven by {AnswerBuilder.ShortName(driver.TypeName)} (+{AnswerBuilder.Mb(driver.DeltaBytes)}, " +
                      $"{driver.LeftCount:N0} → {driver.RightCount:N0} instances).");
            verdictFinding = new ProfilingFinding(
                "change-heap-growth", "warning", "The heap grew between the two snapshots",
                $"Heap {AnswerBuilder.Mb(total.Left)} → {AnswerBuilder.Mb(total.Right)} ({AnswerBuilder.Pct(total.Percent)})" +
                (driver is null ? "." : $"; {driver.TypeName} +{AnswerBuilder.Mb(driver.DeltaBytes)}."),
                driver is null
                    ? "Narrow the window between the snapshots or take a dump to inspect roots."
                    : "Bound or release that type, then compare a third snapshot.",
                driver is null ? null : $"profile_heap focusType='{driver.TypeName}'",
                null);
        }
        else if (verdict == "improvement" && total is not null)
        {
            answer =
                $"Improvement: the managed heap fell from {AnswerBuilder.Mb(total.Left)} to {AnswerBuilder.Mb(total.Right)} " +
                $"({AnswerBuilder.Pct(total.Percent)}, {AnswerBuilder.Mb(total.Delta)}); keep the change.";
            verdictFinding = new ProfilingFinding(
                "change-heap-shrink", "info", "The heap shrank between the two snapshots",
                $"Heap {AnswerBuilder.Mb(total.Left)} → {AnswerBuilder.Mb(total.Right)} ({AnswerBuilder.Pct(total.Percent)}).",
                "Keep the change; confirm the level is stable with a third snapshot.",
                null,
                null);
        }
        else
        {
            answer = total is null
                ? "No heap metric was produced by the two snapshots."
                : $"No meaningful heap change: {AnswerBuilder.Mb(total.Left)} → {AnswerBuilder.Mb(total.Right)} " +
                  $"({AnswerBuilder.Pct(total.Percent)}), under the 10 MB / 20 % floor this review uses.";
            verdictFinding = new ProfilingFinding(
                "change-heap-unchanged", "info", "The heap is stable between the two snapshots",
                total is null ? "No heap bytes metric." :
                    $"Heap {AnswerBuilder.Mb(total.Left)} → {AnswerBuilder.Mb(total.Right)} ({AnswerBuilder.Pct(total.Percent)}).",
                "No action needed for the heap.",
                null,
                null);
        }

        var evidence = new List<AnswerEvidence>
        {
            new(1, 1.0, verdictFinding.Id, verdictFinding.Severity, verdictFinding.Title, verdictFinding.Evidence,
                verdictFinding.Recommendation, verdictFinding.SuggestedAction, verdictFinding.Source),
        };
        evidence.AddRange(AnswerBuilder.Rank(
            comparison.Findings.Select(f => new WeightedFinding(ComparisonWeight(f.Id), f))
                .Where(s => s.Weight > 0 && s.Finding.Id != verdictFinding.Id),
            startRank: 2));

        var reviewSteps = ImprovementOrRegressionSteps(verdict)
            .Concat(comparison.Findings.Count > 0
                ? _advisor.NextSteps(comparison.Findings)
                : Array.Empty<string>())
            .ToList();

        return new ChangeReviewReport(
            new AnswerReport(
                "What changed since the last measurement?",
                verdict,
                answer,
                "high",
                evidence,
                AnswerBuilder.NextSteps(evidence, reviewSteps),
                Measurements(normalized, comparison),
                notes),
            "heap",
            comparison.LeftPath,
            comparison.RightPath,
            true,
            normalized,
            notes,
            comparison);
    }

    private static NormalizedMetric Normalize(string name, string unit, double baseline, double current, bool comparable) =>
        new(name, unit, Math.Round(baseline, 2), Math.Round(current, 2), Math.Round(current - baseline, 2),
            Percent(baseline, current), comparable);

    private static double Floor(string name) => Floors.TryGetValue(name, out var floor) ? floor : 1;

    private static double Percent(double left, double right)
    {
        if (Math.Abs(left) < 0.0001)
            return Math.Abs(right) < 0.0001 ? 0 : 100;
        return Math.Round((right - left) * 100.0 / left, 2);
    }

    private static double ComparisonWeight(string id) => id switch
    {
        "cpu-regression" or "cpu-total-regression" => 1.0,
        "allocation-regression" or "heap-growth" or "heap-type-growth" => 0.8,
        "gc-regression" or "finalizer-growth" => 0.6,
        "exception-regression" or "duplicate-strings-growth" => 0.5,
        "cpu-total-improvement" => 0.3,
        _ => 0.2,
    };

    private static string[] ImprovementOrRegressionSteps(string verdict) => verdict switch
    {
        "regression" => new[]
        {
            "Compare the two revisions with git_diff, then re-measure with the same duration and profile to confirm the fix.",
        },
        "improvement" => new[] { "Keep the change; the next review should use the same duration and profile." },
        "unchanged" => new[] { "No action; if you expected a difference, collect a longer window under the same load." },
        _ => new[] { "Collect both traces for at least 2 seconds with the same profile, then review again." },
    };

    private static IReadOnlyList<string> Measurements(
        IReadOnlyList<NormalizedMetric> normalized, ProfileComparison comparison)
    {
        var lines = normalized
            .Select(m => $"{m.Name}: {m.Baseline:N1} → {m.Current:N1} {m.Unit} ({AnswerBuilder.Pct(m.Percent)})" +
                         (m.Comparable ? "" : " [not comparable]"))
            .ToList();
        if (lines.Count == 0)
            lines.Add("no comparable metric");
        lines.Add($"findings: {comparison.Findings.Count}, method deltas: {comparison.Cpu.Count}, type deltas: " +
                  $"{comparison.Allocations.Count + comparison.Heap.Count}");
        return lines;
    }
}
