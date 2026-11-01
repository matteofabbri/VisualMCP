namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// The whole answer of the comparison: the signals with their direction, the one-word verdict, and the
/// files that moved most in each direction plus the ones that appeared or disappeared.
/// </summary>
public sealed record QualityTrendDiff(
    IReadOnlyList<TrendSignal> Signals,
    string Verdict,
    IReadOnlyList<FileQualityDelta> WorseFiles,
    IReadOnlyList<FileQualityDelta> BetterFiles,
    IReadOnlyList<string> AddedFiles,
    IReadOnlyList<string> RemovedFiles,
    int FilesCompared);
