using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Snitch.Execution;
using Snitch.Git;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// The runtime half of the analysis: runs the test suite on the working tree and on a git revision
/// (checked out in a detached worktree) and diffs the outcome test by test. A static analysis can prove
/// that code compiles; this is what measures whether behaviour changed — regressions, fixes, tests that
/// appeared or disappeared — and says "unverified" when no test covers the run.
/// </summary>
public interface IVerifyChangeService
{
    Task<object> VerifyAsync(string? baseRef, string? projectName, string? testFilter, bool includeBaseline, int timeoutSeconds);
}

public sealed class VerifyChangeService : IVerifyChangeService
{
    private readonly IRoslynWorkspace _workspace;
    public VerifyChangeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> VerifyAsync(
        string? baseRef = null,
        string? projectName = null,
        string? testFilter = null,
        bool includeBaseline = true,
        int timeoutSeconds = 900)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var (repoDir, repoError) = await GitCli.ResolveRepoAsync(null, _workspace.LoadedSolutionPath);
        if (repoError is not null) return repoError;

        var reference = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef.Trim();
        var (revExit, _, revOut, revErr, _) = await GitCli.RunAsync(repoDir!, $"rev-parse --verify \"{reference}^{{commit}}\"", 20);
        if (revExit != 0) return new { error = $"'{reference}' is not a revision in {repoDir}. {revErr.Trim()}".Trim() };
        var commit = revOut.Trim();

        var testProject = ResolveTestProject(solution, projectName, out var resolveError);
        if (testProject is null) return new { error = resolveError };

        var csproj = testProject.FilePath!;
        var relativeProject = Path.GetRelativePath(repoDir!, csproj);
        var timeout = Math.Clamp(timeoutSeconds < 30 ? 900 : timeoutSeconds, 30, 3600);

        var (statusExit, _, statusOut, statusErr, _) = await GitCli.RunAsync(repoDir!, "status --porcelain", 30);
        if (statusExit != 0) return new { error = $"Could not read the working tree state: {statusErr.Trim()}" };

        var changedFiles = statusOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Length > 3 ? line.TrimEnd('\r')[3..].Trim() : line.TrimEnd('\r').Trim())
            .Where(path => path.Length > 0)
            .ToList();

        var current = await RunSuiteAsync(csproj, Path.GetDirectoryName(csproj)!, testFilter, timeout);
        if (current.Error is not null)
            return new
            {
                verdict = "inconclusive",
                phase = "working tree",
                error = current.Error,
                testProject = testProject.Name,
                output = ProcessRunner.Truncate(current.Output, 4000),
            };

        if (includeBaseline && changedFiles.Count == 0)
            return Summarize("nothing-to-verify", reference, commit, testProject.Name, testFilter,
                current, baseline: null, comparison: null, changedFiles,
                note: $"The working tree matches '{reference}': there is no change to compare, so the suite was run once. " +
                      "Commit or modify something first, or pass baseRef to compare against another revision.");

        if (!includeBaseline)
            return Summarize("current-only", reference, commit, testProject.Name, testFilter,
                current, baseline: null, comparison: null, changedFiles,
                note: "Baseline comparison was not requested: these are the outcomes of the working tree only. " +
                      "Pass includeBaseline=true (the default) to run the same suite on the reference revision in a detached worktree.");

        var worktree = Path.Combine(Path.GetTempPath(), "vmcp-baseline-" + Guid.NewGuid().ToString("N"));
        var (addExit, _, _, addErr, _) = await GitCli.RunAsync(repoDir!, $"worktree add --detach \"{worktree}\" {commit}", 180);
        if (addExit != 0)
            return new
            {
                verdict = "inconclusive",
                phase = "baseline worktree",
                error = $"Could not create a worktree at {commit}: {addErr.Trim()}",
                testProject = testProject.Name,
            };

        try
        {
            var baselineProject = Path.Combine(worktree, relativeProject);
            if (!File.Exists(baselineProject))
                return Summarize("inconclusive", reference, commit, testProject.Name, testFilter,
                    current, baseline: null, comparison: null, changedFiles,
                    note: $"{relativeProject} does not exist at '{reference}': the test project is newer than the baseline.");

            var baseline = await RunSuiteAsync(baselineProject, Path.GetDirectoryName(baselineProject)!, testFilter, timeout);
            if (baseline.Error is not null)
                return new
                {
                    verdict = "inconclusive",
                    phase = "baseline",
                    error = "The suite could not run at the reference revision: " + baseline.Error,
                    reference = commit,
                    testProject = testProject.Name,
                    current = current.Summary(),
                    output = ProcessRunner.Truncate(baseline.Output, 4000),
                };

            var comparison = Compare(baseline.Results, current.Results);
            return Summarize(comparison.Verdict, reference, commit, testProject.Name, testFilter,
                current, baseline, comparison, changedFiles, note: ComparisonNote(comparison.Verdict));
        }
        finally
        {
            await GitCli.RunAsync(repoDir!, $"worktree remove --force \"{worktree}\"", 180);
            try { if (Directory.Exists(worktree)) Directory.Delete(worktree, true); } catch { /* best-effort */ }
        }
    }

    private static object Summarize(
        string verdict,
        string reference,
        string commit,
        string testProject,
        string? filter,
        SuiteRun current,
        SuiteRun? baseline,
        Comparison? comparison,
        IReadOnlyList<string> changedFiles,
        string note) =>
        new
        {
            verdict,
            referenceRef = reference,
            reference = commit,
            testProject,
            filter = filter ?? "all tests",
            changedFiles,
            changedFileCount = changedFiles.Count,
            baseline = baseline is null ? null : baseline.Summary(),
            current = current.Summary(),
            counts = comparison is null ? null : new
            {
                regressions = comparison.Regressions.Count,
                fixes = comparison.Fixes.Count,
                stillFailing = comparison.StillFailing.Count,
                newTests = comparison.NewTests.Count,
                removedTests = comparison.RemovedTests.Count,
                unchangedPassed = comparison.UnchangedPassed,
            },
            regressions = comparison?.Regressions ?? [],
            fixes = comparison?.Fixes ?? [],
            newTests = comparison?.NewTests ?? [],
            removedTests = comparison?.RemovedTests ?? [],
            stillFailing = comparison?.StillFailing ?? [],
            note,
        };

    /// <summary>The diff of two runs, test by test. Public so the comparison is testable without a build.</summary>
    public sealed record Comparison(
        string Verdict,
        IReadOnlyList<string> Regressions,
        IReadOnlyList<string> Fixes,
        IReadOnlyList<string> StillFailing,
        IReadOnlyList<string> NewTests,
        IReadOnlyList<string> RemovedTests,
        int UnchangedPassed);

    public static Comparison Compare(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current)
    {
        var regressions = new List<string>();
        var fixes = new List<string>();
        var stillFailing = new List<string>();
        var newTests = new List<string>();
        var removedTests = new List<string>();
        var unchangedPassed = 0;

        foreach (var (name, outcome) in current)
        {
            if (!baseline.TryGetValue(name, out var before))
            {
                newTests.Add(name);
                continue;
            }

            var passedBefore = IsPassed(before);
            var passesNow = IsPassed(outcome);

            if (passedBefore && passesNow) unchangedPassed++;
            else if (passedBefore && !passesNow) regressions.Add($"{name} ({before} -> {outcome})");
            else if (!passedBefore && passesNow) fixes.Add($"{name} ({before} -> {outcome})");
            else stillFailing.Add($"{name} ({outcome})");
        }

        foreach (var name in baseline.Keys)
            if (!current.ContainsKey(name)) removedTests.Add(name);

        var verdict = current.Count == 0
            ? "unverified"
            : regressions.Count > 0
                ? "regression"
                : "behaviour-preserved";

        return new Comparison(verdict, regressions, fixes, stillFailing, newTests, removedTests, unchangedPassed);
    }

    private static string ComparisonNote(string verdict) => verdict switch
    {
        "behaviour-preserved" =>
            "No test that passed at the reference revision fails now. That is measured evidence, not a proof: " +
            "behaviour the suite does not cover is outside this answer.",
        "regression" =>
            "At least one test that passed at the reference revision does not pass now. The list names each one " +
            "with its outcome change; the change is the suspect.",
        "unverified" =>
            "The filter ran no test, so behaviour is unverified: broadeny the filter or add a test that reaches the change.",
        _ => "The two runs could not be compared.",
    };

    private static bool IsPassed(string outcome) =>
        outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase);

    private sealed record SuiteRun(
        IReadOnlyDictionary<string, string> Results,
        bool Ran,
        string? Error,
        string Output)
    {
        public object Summary()
        {
            var passed = Results.Count(entry => IsPassed(entry.Value));
            return new
            {
                ran = Ran,
                total = Results.Count,
                passed,
                failed = Results.Count(entry => entry.Value.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
                skipped = Results.Count(entry => entry.Value.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                                                 entry.Value.Equals("Skipped", StringComparison.OrdinalIgnoreCase)),
            };
        }
    }

    private static async Task<SuiteRun> RunSuiteAsync(string projectPath, string workingDirectory, string? filter, int timeoutSeconds)
    {
        var results = Path.Combine(Path.GetTempPath(), "vmcp-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(results);

        try
        {
            var arguments = new List<string>
            {
                "test", projectPath,
                "--logger", "trx;LogFileName=verify.trx",
                "--results-directory", results,
            };
            if (!string.IsNullOrWhiteSpace(filter)) { arguments.Add("--filter"); arguments.Add(filter.Trim()); }

            var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
                "dotnet", string.Join(" ", arguments.Select(Quote)), workingDirectory, timeoutSeconds);

            var trx = Directory.Exists(results)
                ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories).FirstOrDefault()
                : null;

            if (trx is null)
            {
                var reason = timedOut
                    ? $"The suite did not finish within {timeoutSeconds}s."
                    : exitCode != 0 && (stdout + stderr).Contains("error", StringComparison.OrdinalIgnoreCase)
                        ? "The build or the test run failed before producing a result."
                        : "No TRX result was produced (the filter matched nothing, or the project is not a test project).";
                return new SuiteRun(new Dictionary<string, string>(), false, reason, (stdout + "\n" + stderr).Trim());
            }

            var outcomes = ParseTrx(await File.ReadAllTextAsync(trx));
            return new SuiteRun(outcomes, outcomes.Count > 0, null, (stdout + "\n" + stderr).Trim());
        }
        finally
        {
            try { Directory.Delete(results, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Test name to outcome, from a TRX file. Public so the format is pinned by a test.</summary>
    public static Dictionary<string, string> ParseTrx(string xml)
    {
        var outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xml)) return outcomes;

        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return outcomes; }

        foreach (var result in document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            var name = result.Attribute("testName")?.Value;
            var outcome = result.Attribute("outcome")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(outcome)) continue;

            // A retried test has several results; the last attempt is the one that counts.
            outcomes[name] = outcome;
        }

        return outcomes;
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static Project? ResolveTestProject(Solution solution, string? projectName, out string? error)
    {
        error = null;
        var projects = solution.Projects.Where(project => project.FilePath is not null).ToList();

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var named = projects.FirstOrDefault(project =>
                string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;

            error = $"Project '{projectName}' not found in the loaded solution.";
            return null;
        }

        var candidates = projects
            .Where(project => project.Name.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                              (project.AssemblyName ?? string.Empty).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 1) return candidates[0];

        error = candidates.Count == 0
            ? "No test project found in the solution: pass projectName (the project whose suite verifies the change)."
            : $"{candidates.Count} test projects found: {string.Join(", ", candidates.Select(project => project.Name))}. Pass projectName.";
        return null;
    }
}
