namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// What one source file contributes to a side of the comparison. The aggregates are derived here so the
/// same file object can answer for itself without the revision totals being recomputed per question.
/// </summary>
public sealed record FileQuality(
    string Path,
    int Lines,
    int SourceLines,
    int Types,
    int Todos,
    IReadOnlyList<MethodQuality> Methods)
{
    public int Complexity => Methods.Sum(method => method.Complexity);

    public int LongMethods => Methods.Count(method => method.Lines > SourceQualityReader.LongMethodLines);

    public int MethodsOver(int threshold) => Methods.Count(method => method.Complexity > threshold);

    public int MaxMethodComplexity => Methods.Count == 0 ? 0 : Methods.Max(method => method.Complexity);

    /// <summary>The method that owns the file's highest complexity — the one to open first.</summary>
    public string? WorstMethod => Methods
        .OrderByDescending(method => method.Complexity)
        .ThenBy(method => method.Line)
        .FirstOrDefault()?.Name;
}
