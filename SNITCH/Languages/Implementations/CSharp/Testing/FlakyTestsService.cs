using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// Flaky detection: runs the same tests several times and reports the ones whose outcome changed between
/// runs. A test that is always red is not flaky — it is broken — so the two are reported apart.
/// </summary>
public interface IFlakyTestsService
{
    Task<object> FindAsync(string? projectName, string? testName, string? className, string? filter, int iterations, int timeoutSeconds);
}

public sealed class FlakyTestsService : IFlakyTestsService
{
    private readonly IRoslynWorkspace _workspace;
    public FlakyTestsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FindAsync(
        string? projectName = null, string? testName = null, string? className = null, string? filter = null,
        int iterations = 3, int timeoutSeconds = 300)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-loaded from the working directory." };

        var project = TestSuiteRunner.ResolveTestProject(solution, projectName, out var resolveError);
        if (project is null) return new { error = resolveError };

        var composed = TestSuiteRunner.ComposeFilter(testName, className, filter);
        var runs = Math.Clamp(iterations < 2 ? 3 : iterations, 2, 10);
        var limit = Math.Clamp(timeoutSeconds < 30 ? 300 : timeoutSeconds, 30, 1800);

        var outcomes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var summaries = new List<object>();

        for (var run = 1; run <= runs; run++)
        {
            var result = await TestSuiteRunner.RunAsync(project.FilePath!, composed, limit);
            if (!result.Ran)
                return new
                {
                    error = $"Run {run} produced no test result: {result.Error}",
                    run,
                    output = Snitch.Execution.ProcessRunner.Truncate(result.Output, 4000),
                };

            summaries.Add(new { run, passed = result.Passed, failed = result.Failed, skipped = result.Skipped, tests = result.Outcomes.Count });

            foreach (var (name, outcome) in result.Outcomes)
            {
                if (!outcomes.TryGetValue(name, out var list))
                {
                    list = [];
                    outcomes[name] = list;
                }

                list.Add(outcome);
            }
        }

        var classification = Classify(outcomes, runs);

        return new
        {
            project = project.Name,
            filter = composed ?? "all tests",
            iterations = runs,
            passedRuns = summaries.Count,
            runs = summaries,
            flakyCount = classification.Flaky.Count,
            flaky = classification.Flaky.Select(entry => new { test = entry.Test, outcomes = entry.Outcomes, runs = entry.Outcomes.Count }).ToList(),
            alwaysFailing = classification.AlwaysFailing.Take(20).ToList(),
            alwaysFailingCount = classification.AlwaysFailing.Count,
            unstablePresence = classification.Unstable.Select(entry => new { test = entry.Test, seenIn = entry.SeenIn, runs }),
            note = classification.Flaky.Count == 0
                ? "No test changed outcome across the runs. That is evidence of stability for these iterations, not proof."
                : "A flaky test is one that passed in some runs and failed in others; alwaysFailing lists the tests that " +
                  "never passed, which are broken rather than flaky.",
        };
    }

    public sealed record FlakyEntry(string Test, IReadOnlyList<string> Outcomes);

    public sealed record FlakyClassification(
        IReadOnlyList<FlakyEntry> Flaky,
        IReadOnlyList<string> AlwaysFailing,
        IReadOnlyList<(string Test, int SeenIn)> Unstable);

    /// <summary>
    /// Flaky = passed in some runs and not in others (a failure or a skip both count as "not passed");
    /// always failing = never passed; unstable presence = a test that did not run in every iteration (a
    /// filter or a discovery failure, not flakiness).
    /// </summary>
    public static FlakyClassification Classify(IReadOnlyDictionary<string, List<string>> outcomes, int runs)
    {
        var flaky = new List<FlakyEntry>();
        var alwaysFailing = new List<string>();
        var unstable = new List<(string, int)>();

        foreach (var (name, list) in outcomes)
        {
            var hasPass = list.Any(SuiteResult.IsPassed);
            var hasNotPass = list.Any(outcome => !SuiteResult.IsPassed(outcome));

            if (hasPass && hasNotPass) flaky.Add(new FlakyEntry(name, list));
            else if (!hasPass) alwaysFailing.Add(name);
            else if (list.Count < runs) unstable.Add((name, list.Count));
        }

        return new FlakyClassification(flaky, alwaysFailing, unstable);
    }
}
