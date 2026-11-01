namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// One file present on both sides, with what changed. A positive <see cref="Delta"/> means the file got
/// harder to read; the caller keeps the increases and the decreases apart.
/// </summary>
public sealed record FileQualityDelta(
    string File,
    int ComplexityBefore,
    int ComplexityAfter,
    int MethodsBefore,
    int MethodsAfter)
{
    public int Delta => ComplexityAfter - ComplexityBefore;
}
