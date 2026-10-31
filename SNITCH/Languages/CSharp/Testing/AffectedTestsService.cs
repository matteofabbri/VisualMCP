using System.Text.Json;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// Test impact analysis that also runs: it asks which tests the change touches, then executes exactly
/// those — the loop a developer repeats by hand from the impact report.
/// </summary>
public interface IAffectedTestsService
{
    Task<object> RunAsync(string? baseRef, string? projectName, string? testProjectName, int timeoutSeconds);
}

public sealed class AffectedTestsService : IAffectedTestsService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly ITestWorkflowService _workflow;

    public AffectedTestsService(IRoslynWorkspace workspace, ITestWorkflowService workflow)
    {
        _workspace = workspace;
        _workflow = workflow;
    }

    public async Task<object> RunAsync(
        string? baseRef = null, string? projectName = null, string? testProjectName = null, int timeoutSeconds = 300)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-loaded from the working directory." };

        var impact = JsonSerializer.SerializeToElement(
            await _workflow.ImpactAnalysisAsync(baseRef, projectName, 1000));

        if (impact.TryGetProperty("error", out var error))
            return new { error = error.GetString(), phase = "impact analysis" };

        var filter = impact.TryGetProperty("filter", out var filterElement) ? filterElement.GetString() : null;
        var count = impact.TryGetProperty("count", out var countElement) ? countElement.GetInt32() : 0;
        var changed = impact.TryGetProperty("changedFiles", out var changedElement) ? changedElement.GetInt32() : 0;

        if (count == 0 || string.IsNullOrWhiteSpace(filter))
            return new
            {
                verdict = "no-affected-tests",
                baseRef = impact.TryGetProperty("baseRef", out var reference) ? reference.GetString() : baseRef,
                changedFiles = changed,
                coveredTests = count,
                note = "No test references a symbol declared by the changed files: nothing to run for this change. " +
                       "That can also mean the change is untested — check the diff.",
            };

        var run = await _workflow.RunTestAsync(null, null, testProjectName, filter, timeoutSeconds);

        return new
        {
            verdict = "ran",
            baseRef = impact.TryGetProperty("baseRef", out var reference2) ? reference2.GetString() : baseRef,
            changedFiles = changed,
            affectedTests = count,
            filter,
            run,
            note = "The filter is composed from the test methods that reference the changed symbols; the run result is " +
                   "run_test's own shape (TRX: passed, failed, failure messages).",
        };
    }
}
