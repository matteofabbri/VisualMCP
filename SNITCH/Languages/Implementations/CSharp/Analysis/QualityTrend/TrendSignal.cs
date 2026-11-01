namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// One metric compared between the two sides. <see cref="Direction"/> is the judgement: <c>better</c>,
/// <c>worse</c> or <c>neutral</c> — the context metrics (size, file count) are always neutral, they only
/// explain why a complexity total moved.
/// </summary>
public sealed record TrendSignal(
    string Metric,
    double Before,
    double After,
    double Delta,
    double? DeltaPercent,
    string Direction);
