namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// Measures two sides of history with the same rule set and reports the delta: the answer to "did this
/// branch make the code better or worse", which the public-API comparison cannot give.
/// </summary>
public interface IQualityTrendService
{
    Task<object> MeasureAsync(
        string? fromRef,
        string? toRef,
        string? repoPath,
        string? path,
        int top,
        CancellationToken cancellationToken = default);
}
