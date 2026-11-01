using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

public interface IFixComplexityService
{
    Task<object> FixComplexity(
        string? projectName = null,
        string? filePath = null,
        int maxMethods = 5,
        int target = 10,
        int maxStepsPerMethod = 6,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The one-call entry point over the complexity family: it ranks every method the way
/// <c>complexity_report</c> does, lowers the worst of them with <c>reduce_complexity</c>'s reducer,
/// and aggregates the runs into a single before/after report. The agent does not have to orchestrate
/// "rank, then drill into each, then reduce, then re-check" itself.
/// <para>
/// Each method is still compiler-gated by the reducer, so a run can report several full project
/// compilations; that cost is what makes the writes safe.
/// </para>
/// </summary>
public sealed class FixComplexityService : IFixComplexityService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IComplexityReportService _report;
    private readonly IReduceComplexityService _reducer;

    public FixComplexityService(
        IRoslynWorkspace workspace,
        IComplexityReportService report,
        IReduceComplexityService reducer)
    {
        _workspace = workspace;
        _report = report;
        _reducer = reducer;
    }

    public async Task<object> FixComplexity(
        string? projectName = null,
        string? filePath = null,
        int maxMethods = 5,
        int target = 10,
        int maxStepsPerMethod = 6,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (target < 1) return new { error = "The target must be at least 1." };
        if (maxMethods < 1) maxMethods = 1;

        // The ranking threshold is the target itself: a method qualifies when either metric is above
        // it, which is exactly when the reducer (which wants both at or below) has work to do.
        var ranking = await _report.RankMetricsAsync(projectName, filePath, target + 1, cancellationToken);
        if (ranking.Error is not null) return new { error = ranking.Error };

        var candidates = ranking.Metrics.Take(maxMethods).ToList();
        if (candidates.Count == 0)
            return new
            {
                @refactoring = "fix_complexity",
                scope = new { project = projectName ?? "all", file = filePath },
                target,
                dryRun,
                changed = false,
                before = Snapshot(ranking.Metrics),
                after = (object?)null,
                totals = new
                {
                    considered = 0,
                    lowered = 0,
                    needAHuman = 0,
                    skipped = 0,
                    cognitiveRemoved = 0,
                    cyclomaticRemoved = 0,
                },
                summary = $"Nothing to do: no method is above the target of {target}.",
                methods = Array.Empty<object>(),
                skipped = Array.Empty<object>(),
                next = "complexity_report ranks the solution in full; analyze_complexity is the different question (Big-O, not paths).",
            };

        var fixes = new List<Fix>();
        var skipped = new List<object>();
        var cognitiveRemoved = 0;
        var cyclomaticRemoved = 0;

        foreach (var candidate in candidates)
        {
            // Earlier steps can insert a private method above the next target in the same file, so the
            // ranked line may be stale. The full signature (name + parameter types + type) is stable.
            var line = await LocateAsync(candidate, cancellationToken);
            if (line is null)
            {
                skipped.Add(new
                {
                    name = candidate.Name,
                    file = candidate.FilePath,
                    line = candidate.Line,
                    reason = "the method is no longer in the solution (an earlier step may have moved it)",
                });
                continue;
            }

            var outcome = await _reducer.ReduceAsync(
                new ReduceComplexityRequest(candidate.FilePath!, line, Target: target, MaxSteps: maxStepsPerMethod, DryRun: dryRun),
                cancellationToken);

            if (outcome.Error is not null)
            {
                skipped.Add(new { name = candidate.Name, file = candidate.FilePath, line = candidate.Line, reason = outcome.Error });
                continue;
            }

            if (outcome.Applied)
            {
                cognitiveRemoved += Math.Max(0, candidate.Cognitive - (outcome.After?.Cognitive ?? candidate.Cognitive));
                cyclomaticRemoved += Math.Max(0, candidate.Cyclomatic - (outcome.After?.Cyclomatic ?? candidate.Cyclomatic));
            }

            fixes.Add(new Fix(candidate, outcome));
        }

        var lowered = fixes.Count(fix => fix.Result.Applied);
        var needAHuman = fixes.Count - lowered;

        // A written run can say what is left; a preview has not changed the solution, so it cannot.
        ComplexityRanking? remaining = null;
        if (!dryRun)
        {
            var after = await _report.RankMetricsAsync(projectName, filePath, target + 1, cancellationToken);
            if (after.Error is null) remaining = after;
        }

        return new
        {
            @refactoring = "fix_complexity",
            scope = new { project = projectName ?? "all", file = filePath },
            target,
            dryRun,
            changed = lowered > 0,
            before = Snapshot(ranking.Metrics),
            after = remaining is null ? null : Snapshot(remaining.Metrics),
            totals = new
            {
                considered = candidates.Count,
                lowered,
                needAHuman,
                skipped = skipped.Count,
                cognitiveRemoved,
                cyclomaticRemoved,
            },
            summary = Summary(lowered, candidates.Count, needAHuman, cognitiveRemoved, cyclomaticRemoved, target, dryRun),
            methods = fixes.Select(Describe).ToList(),
            skipped,
            next = Next(lowered, needAHuman, skipped.Count, remaining, target, dryRun),
        };
    }

    private sealed record Fix(ComplexityMetrics Candidate, ReduceComplexityOutcome Result);

    private static object Snapshot(IReadOnlyList<ComplexityMetrics> metrics) => new
    {
        methodsAboveTarget = metrics.Count,
        worstCognitive = metrics.Count == 0 ? 0 : metrics.Max(metric => metric.Cognitive),
        worstCyclomatic = metrics.Count == 0 ? 0 : metrics.Max(metric => metric.Cyclomatic),
    };

    private static object Describe(Fix fix) => new
    {
        name = fix.Candidate.Name,
        fullName = fix.Candidate.FullName,
        file = fix.Candidate.FilePath,
        line = fix.Candidate.Line,
        before = new { cognitive = fix.Candidate.Cognitive, cyclomatic = fix.Candidate.Cyclomatic, risk = fix.Candidate.Risk },
        after = fix.Result.After is null
            ? null
            : new { cognitive = fix.Result.After.Cognitive, cyclomatic = fix.Result.After.Cyclomatic, risk = fix.Result.After.Risk },
        applied = fix.Result.Applied,
        steps = fix.Result.Steps,
        rejected = fix.Result.Rejected,
        compilerErrors = new { before = fix.Result.CompilerErrorsBefore, after = fix.Result.CompilerErrorsAfter },
        preview = fix.Result.Preview,
        note = fix.Result.Note,
        manualSuggestions = fix.Result.Suggestions
            .Where(suggestion => !suggestion.AutoApplicable)
            .Select(suggestion => new
            {
                suggestion.Kind,
                suggestion.Line,
                suggestion.Description,
                suggestion.BlockedBy,
            })
            .ToList(),
    };

    private static string Summary(
        int lowered, int considered, int needAHuman, int cognitiveRemoved, int cyclomaticRemoved, int target, bool dryRun)
    {
        if (lowered == 0)
            return needAHuman == 0
                ? "No method was lowered: nothing auto-applicable was found."
                : $"No method could be lowered automatically; {needAHuman} need a human (see manualSuggestions).";

        var change = $"cognitive -{cognitiveRemoved}, cyclomatic -{cyclomaticRemoved}";
        return dryRun
            ? $"Preview: {lowered} of {considered} methods can be lowered ({change}); pass dryRun=false to write."
            : $"Lowered {lowered} of {considered} methods ({change}).";
    }

    private static string Next(
        int lowered, int needAHuman, int skipped, ComplexityRanking? remaining, int target, bool dryRun)
    {
        var steps = new List<string>();

        if (dryRun && lowered > 0) steps.Add("pass dryRun=false to write the previewed changes");
        if (remaining is not null && remaining.Metrics.Count > 0)
            steps.Add($"{remaining.Metrics.Count} method(s) are still above {target}: call fix_complexity again to continue");
        if (needAHuman > 0)
            steps.Add("complexity_report on the ones with nothing auto-applicable shows the decision points and the moves only a human can make");
        if (skipped > 0) steps.Add("the skipped entries name what went stale or could not be read");

        return steps.Count == 0 ? "Nothing left above the target." : string.Join("; ", steps) + ".";
    }

    /// <summary>
    /// The line to pass to the reducer. Line numbers are not stable while this run writes files, but a
    /// display signature is: it carries the name, the parameter types and the containing type, so an
    /// overload cannot be mistaken for its sibling.
    /// </summary>
    private async Task<int?> LocateAsync(ComplexityMetrics candidate, CancellationToken cancellationToken)
    {
        if (candidate.FilePath is null) return candidate.Line;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return null;

        var path = Path.GetFullPath(candidate.FilePath);
        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidateDocument => candidateDocument.FilePath is not null &&
                                                 string.Equals(Path.GetFullPath(candidateDocument.FilePath), path, HostSystem.PathComparison));
        if (document is null) return null;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return null;

        var matches = ComplexityAnalyzer.Units(root)
            .Select(unit => ComplexityAnalyzer.Analyze(unit, model, document.FilePath))
            .Where(metrics => string.Equals(metrics.FullName, candidate.FullName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0].Line;

        // Lambdas have no declared symbol, so they share the display name "lambda": fall back to the
        // occurrence closest to where the ranking saw this one.
        return matches.OrderBy(metrics => Math.Abs(metrics.Line - candidate.Line)).First().Line;
    }
}
