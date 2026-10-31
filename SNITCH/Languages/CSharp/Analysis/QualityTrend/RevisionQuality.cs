namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// One side of the comparison: a commit, or the working tree, with every analysed file and the totals the
/// trend reads. The totals are computed over the same rule set on both sides, so the deltas compare like
/// with like even though the two sides were never compiled.
/// </summary>
public sealed record RevisionQuality(
    string Revision,
    string Label,
    IReadOnlyDictionary<string, FileQuality> Files)
{
    public int FileCount => Files.Count;

    public int Lines => Files.Values.Sum(file => file.Lines);

    public int SourceLines => Files.Values.Sum(file => file.SourceLines);

    public int TypeCount => Files.Values.Sum(file => file.Types);

    public int MethodCount => Files.Values.Sum(file => file.Methods.Count);

    public int Complexity => Files.Values.Sum(file => file.Complexity);

    public int LongMethods => Files.Values.Sum(file => file.LongMethods);

    public int Todos => Files.Values.Sum(file => file.Todos);

    public int MethodsOver(int threshold) =>
        Files.Values.Sum(file => file.MethodsOver(threshold));

    public int MaxMethodComplexity => Files.Count == 0 ? 0 : Files.Values.Max(file => file.MaxMethodComplexity);

    /// <summary>
    /// Complexity per method. This is the metric a refactoring or a feature moves in a way that does not
    /// simply track how much code was added, which is why the verdict is built on it.
    /// </summary>
    public double AverageComplexity => MethodCount == 0 ? 0 : (double)Complexity / MethodCount;
}
