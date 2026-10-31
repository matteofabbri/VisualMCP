namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// Turns two sides into one answer. The verdict is built only on the metrics a change can be judged by —
/// average complexity per method, the count of methods over 10 and over 20, long methods and TODO markers.
/// Size (files, lines, types, methods) and the complexity total are carried as context with no direction:
/// adding code raises the total without making anything worse.
/// </summary>
public static class QualityTrendComparer
{
    /// <summary>A change smaller than this in average complexity is noise, not a regression.</summary>
    private const double AverageComplexityThresholdPercent = 1.0;

    /// <summary>Complexity at or above which a method is reported as a candidate for a refactoring.</summary>
    public const int ComplexityThreshold = 10;

    public static QualityTrendDiff Compare(RevisionQuality before, RevisionQuality after, int top)
    {
        var limit = Math.Max(1, top);

        var signals = new List<TrendSignal>
        {
            Context("files", before.FileCount, after.FileCount),
            Context("sourceLines", before.SourceLines, after.SourceLines),
            Context("types", before.TypeCount, after.TypeCount),
            Context("methods", before.MethodCount, after.MethodCount),
            Context("complexity", before.Complexity, after.Complexity),
            LowerIsBetter("averageComplexity", before.AverageComplexity, after.AverageComplexity, AverageComplexityThresholdPercent),
            LowerIsBetter("methodsOver10", before.MethodsOver(10), after.MethodsOver(10), null),
            LowerIsBetter("methodsOver20", before.MethodsOver(20), after.MethodsOver(20), null),
            LowerIsBetter("longMethods", before.LongMethods, after.LongMethods, null),
            LowerIsBetter("todos", before.Todos, after.Todos, null),
        };

        var better = signals.Count(signal => signal.Direction == "better");
        var worse = signals.Count(signal => signal.Direction == "worse");

        var verdict = worse > better ? "regressed" : better > worse ? "improved" : "unchanged";

        var shared = before.Files.Keys
            .Intersect(after.Files.Keys, StringComparer.Ordinal)
            .Select(path => new FileQualityDelta(
                path,
                before.Files[path].Complexity,
                after.Files[path].Complexity,
                before.Files[path].Methods.Count,
                after.Files[path].Methods.Count))
            .ToList();

        var worseFiles = shared
            .Where(delta => delta.Delta > 0)
            .OrderByDescending(delta => delta.Delta)
            .ThenBy(delta => delta.File, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var betterFiles = shared
            .Where(delta => delta.Delta < 0)
            .OrderBy(delta => delta.Delta)
            .ThenBy(delta => delta.File, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var addedFiles = after.Files.Keys
            .Except(before.Files.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var removedFiles = before.Files.Keys
            .Except(after.Files.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return new QualityTrendDiff(signals, verdict, worseFiles, betterFiles, addedFiles, removedFiles, shared.Count);
    }

    private static TrendSignal Context(string metric, double before, double after) =>
        new(metric, Round(before), Round(after), Round(after - before), Percent(before, after), "neutral");

    private static TrendSignal LowerIsBetter(string metric, double before, double after, double? thresholdPercent)
    {
        var delta = after - before;
        var percent = Percent(before, after);

        string direction;

        if (thresholdPercent is { } threshold)
        {
            if (percent is { } relative && Math.Abs(relative) >= threshold)
                direction = delta < 0 ? "better" : "worse";
            else if (before == 0 && after > 0)
                direction = "worse";
            else if (before > 0 && after == 0)
                direction = "better";
            else
                direction = "neutral";
        }
        else
        {
            direction = delta < 0 ? "better" : delta > 0 ? "worse" : "neutral";
        }

        return new TrendSignal(metric, Round(before), Round(after), Round(delta), percent, direction);
    }

    private static double? Percent(double before, double after) =>
        before == 0 ? null : Round((after - before) / before * 100);

    private static double Round(double value) => Math.Round(value, 2);
}
