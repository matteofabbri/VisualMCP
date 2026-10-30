namespace Snitch.Languages.CSharp.Analysis.Complexity;

/// <summary>
/// One decision point found inside a method, with the cost it added to each metric and the nesting
/// level it sat at. This is the part a bare score hides: "complexity 24" says nothing about whether
/// it is 24 flat <c>if</c>s or six <c>if</c>s four levels deep, and only the second is a readability
/// problem.
/// </summary>
public sealed record ComplexityContribution(
    string Kind,
    int Line,
    int Nesting,
    int Cyclomatic,
    int Cognitive,
    string? Detail = null);

/// <summary>
/// The metrics professional tooling reports for one method/lambda/local function.
/// <see cref="Cyclomatic"/> is McCabe's path count; <see cref="Cognitive"/> is SonarSource's
/// human-readability score (nesting and broken linear flow cost more than a flat chain of
/// decisions, and a switch is cheaper than an equivalent else-if ladder);
/// <see cref="MaintainabilityIndex"/> is Visual Studio's 0-100 index derived from Halstead volume,
/// complexity and size.
/// </summary>
public sealed record ComplexityMetrics(
    string Name,
    string FullName,
    string? FilePath,
    int Line,
    int EndLine,
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int LinesOfCode,
    int ParameterCount,
    int HalsteadVolume,
    int MaintainabilityIndex,
    string Risk,
    IReadOnlyList<ComplexityContribution> Contributions)
{
    /// <summary>The contributions ranked by how much of the score they own, biggest first.</summary>
    public IEnumerable<ComplexityContribution> WorstContributions(int take) => Contributions
        .OrderByDescending(contribution => contribution.Cognitive)
        .ThenByDescending(contribution => contribution.Nesting)
        .Take(take);
}

/// <summary>
/// A concrete way to lower a method's score, with the reduction it is expected to give. Produced by
/// <see cref="ComplexityPlanner"/> for both the analysis report and the reducer, so the tool that
/// explains and the tool that fixes always agree on what can be done.
/// </summary>
public sealed record ComplexitySuggestion(
    string Kind,
    int Line,
    int EndLine,
    string Description,
    int CyclomaticReduction,
    int CognitiveReduction,
    bool AutoApplicable,
    string? BlockedBy = null);
