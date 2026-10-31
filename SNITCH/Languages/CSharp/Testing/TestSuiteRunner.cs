using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Snitch.Execution;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// Runs a test project once and reads the TRX outcome map: the shared half of the tools that compare
/// runs (flaky detection, continuous testing). Resolution and parsing are public so they are testable
/// without a build.
/// </summary>
public sealed record SuiteResult(
    IReadOnlyDictionary<string, string> Outcomes,
    bool Ran,
    string? Error,
    string Output,
    int ExitCode,
    bool TimedOut)
{
    public int Passed => Outcomes.Values.Count(IsPassed);

    public int Failed => Outcomes.Values.Count(outcome => outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase));

    public int Skipped => Outcomes.Values.Count(outcome =>
        outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) ||
        outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase));

    public static bool IsPassed(string outcome) => outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase);
}

public static class TestSuiteRunner
{
    /// <summary>The test project: the named one, or the single project that looks like a test project.</summary>
    public static Project? ResolveTestProject(Solution solution, string? projectName, out string? error)
    {
        error = null;
        var projects = solution.Projects.Where(project => project.FilePath is not null).ToList();

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var named = projects.FirstOrDefault(project =>
                string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;

            var byPath = projects.FirstOrDefault(project =>
                string.Equals(Path.GetFullPath(project.FilePath!), Path.GetFullPath(projectName!), StringComparison.OrdinalIgnoreCase));
            if (byPath is not null) return byPath;

            error = $"Project '{projectName}' not found in the loaded solution.";
            return null;
        }

        var candidates = projects
            .Where(project => project.Name.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                              (project.AssemblyName ?? string.Empty).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 1) return candidates[0];

        error = candidates.Count == 0
            ? "No test project found in the solution: pass projectName (the project whose tests run)."
            : $"{candidates.Count} test projects found: {string.Join(", ", candidates.Select(project => project.Name))}. Pass projectName.";
        return null;
    }

    /// <summary>The filter a test name or class composes, or the raw filter as written.</summary>
    public static string? ComposeFilter(string? testName, string? className, string? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter)) return filter.Trim();
        if (!string.IsNullOrWhiteSpace(testName))
            return testName!.Contains('.') ? $"FullyQualifiedName={testName.Trim()}" : $"FullyQualifiedName~{testName.Trim()}";
        if (!string.IsNullOrWhiteSpace(className)) return $"FullyQualifiedName~{className!.Trim()}";
        return null;
    }

    /// <summary>Runs the project's tests once (optionally filtered) and parses the TRX result.</summary>
    public static async Task<SuiteResult> RunAsync(
        string projectPath, string? filter, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var workingDirectory = Path.GetDirectoryName(projectPath)!;
        var results = Path.Combine(Path.GetTempPath(), "vmcp-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(results);

        try
        {
            var arguments = new List<string>
            {
                "test", projectPath,
                "--logger", "trx;LogFileName=suite.trx",
                "--results-directory", results,
            };
            if (!string.IsNullOrWhiteSpace(filter)) { arguments.Add("--filter"); arguments.Add(filter); }

            var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
                "dotnet", string.Join(" ", arguments.Select(Quote)), workingDirectory, timeoutSeconds);

            var trx = Directory.Exists(results)
                ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories).FirstOrDefault()
                : null;

            if (trx is null)
            {
                var reason = timedOut
                    ? $"The suite did not finish within {timeoutSeconds}s."
                    : "No TRX result was produced (the filter matched nothing, or the project is not a test project).";
                return new SuiteResult(new Dictionary<string, string>(), false, reason,
                    (stdout + "\n" + stderr).Trim(), exitCode, timedOut);
            }

            var outcomes = VerifyChangeService.ParseTrx(await File.ReadAllTextAsync(trx, cancellationToken));
            return new SuiteResult(outcomes, outcomes.Count > 0, null, (stdout + "\n" + stderr).Trim(), exitCode, timedOut);
        }
        finally
        {
            try { Directory.Delete(results, true); } catch { /* best-effort */ }
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
