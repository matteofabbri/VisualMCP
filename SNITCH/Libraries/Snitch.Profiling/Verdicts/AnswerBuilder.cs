using Snitch.Profiling.Model;

namespace Snitch.Profiling.Verdicts;

/// <summary>A finding with the weight that places it in the ranked evidence list.</summary>
internal sealed record WeightedFinding(double Weight, ProfilingFinding Finding);

/// <summary>
/// Shared machinery for the answer layer: rank evidence by the weight of what it explains, keep the
/// strongest version of a finding, and turn the list into the "what to do next" lines. Every service
/// in this folder uses the same ordering rules so two answers read the same way.
/// </summary>
internal static class AnswerBuilder
{
    /// <summary>
    /// A cause below this weight (10 % of the measured window) does not explain a slowdown. Keeping
    /// 1 % and 9 % on the same footing is what makes "no dominant cause" a useful verdict.
    /// </summary>
    public const double DominantThreshold = 0.10;

    public static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1,
    };

    /// <summary>Severity derived from how much of the window the evidence explains.</summary>
    public static string SeverityFor(double weight) =>
        weight >= 0.75 ? "critical" : weight >= 0.25 ? "warning" : "info";

    /// <summary>
    /// Orders the evidence by weight (desc), then severity, then input order, and numbers it from
    /// <paramref name="startRank"/> (the verdict already holds the ranks before it).
    /// A finding id appears once: the entry that explains more wins, and on a tie the more severe one.
    /// </summary>
    public static IReadOnlyList<AnswerEvidence> Rank(IEnumerable<WeightedFinding> items, int startRank = 1)
    {
        var kept = new Dictionary<string, WeightedFinding>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var item in items)
        {
            var id = item.Finding.Id;
            if (!kept.TryGetValue(id, out var existing))
            {
                kept[id] = item;
                order.Add(id);
                continue;
            }

            if (item.Weight > existing.Weight + 0.0001)
            {
                kept[id] = item;
            }
            else if (Math.Abs(item.Weight - existing.Weight) <= 0.0001 &&
                     SeverityRank(item.Finding.Severity) > SeverityRank(existing.Finding.Severity))
            {
                kept[id] = item;
            }
        }

        var ranked = order
            .Select(id => kept[id])
            .OrderByDescending(i => i.Weight)
            .ThenByDescending(i => SeverityRank(i.Finding.Severity))
            .ToList();

        return ranked
            .Select((item, index) => new AnswerEvidence(
                startRank + index,
                Math.Round(item.Weight, 4),
                item.Finding.Id,
                item.Finding.Severity,
                item.Finding.Title,
                item.Finding.Evidence,
                item.Finding.Recommendation,
                string.IsNullOrWhiteSpace(item.Finding.SuggestedAction) ? null : item.Finding.SuggestedAction,
                item.Finding.Source))
            .ToList();
    }

    /// <summary>
    /// The concrete calls first (a tool call or a file to open), then the recommendations, without
    /// repeats. Capped because a list of twenty next steps is not a next step.
    /// </summary>
    public static IReadOnlyList<string> NextSteps(
        IEnumerable<AnswerEvidence> evidence, IEnumerable<string>? extra = null, int max = 8)
    {
        var steps = new List<string>();
        void Add(string? step, bool dedupe)
        {
            if (string.IsNullOrWhiteSpace(step))
                return;
            var text = step.Trim();
            if (dedupe && steps.Contains(text, StringComparer.OrdinalIgnoreCase))
                return;
            steps.Add(text);
        }

        foreach (var item in evidence)
            Add(item.NextStep, dedupe: true);
        if (extra is not null)
            foreach (var step in extra)
                Add(step, dedupe: true);
        foreach (var item in evidence)
            Add(item.Recommendation, dedupe: true);

        return steps.Take(max).ToList();
    }

    public static string Ms(double value) =>
        Math.Abs(value) >= 1000 ? $"{value / 1000.0:0.0} s" : $"{value:0} ms";

    public static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";

    public static string Mb(double bytes) => $"{bytes / 1024.0 / 1024.0:0.0} MB";

    public static string Pct(double percent) => $"{percent:+0.0;-0.0;0.0}%";

    public static string ShortName(string fullName) => Analysis.FrameNames.ShortName(fullName);

    public static bool IsBlockingFrame(string method) =>
        method.Contains("Monitor.Wait", StringComparison.Ordinal) ||
        method.Contains("Monitor.Enter", StringComparison.Ordinal) ||
        method.Contains("SemaphoreSlim.Wait", StringComparison.Ordinal) ||
        method.Contains("Task.Wait", StringComparison.Ordinal) ||
        method.Contains("Task.Result", StringComparison.Ordinal) ||
        method.Contains("get_Result", StringComparison.Ordinal) ||
        method.Contains("Thread.Sleep", StringComparison.Ordinal) ||
        method.Contains("Thread.Join", StringComparison.Ordinal) ||
        method.Contains("WaitHandle.Wait", StringComparison.Ordinal);

    /// <summary>True when a blocked stack has a thread-pool frame, i.e. a pool worker is the one waiting.</summary>
    public static bool IsThreadPoolFrame(string method) =>
        method.Contains("PortableThreadPool", StringComparison.Ordinal) ||
        method.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal) ||
        method.Contains("Task.ExecuteWithThreadLocal", StringComparison.Ordinal) ||
        method.Contains("ThreadPool", StringComparison.Ordinal);
}
