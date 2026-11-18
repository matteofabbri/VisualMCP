using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Xml.Linq;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

public interface IRunTestsService
{
    Task<object> RunTests(string? projectFilter, string? extraArgs, int timeoutSeconds);
}

public sealed class RunTestsService : IRunTestsService
{
    private readonly IRoslynWorkspace _workspace;
    public RunTestsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RunTests(
        string? projectFilter = null,
        string? extraArgs = null,
        int timeoutSeconds = 600)
    {
        var service  = _workspace;
        var solution = await service.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx." };

        var solutionPath = service.LoadedSolutionPath;
        var testProjects = DetectTestProjects(solution);
        var selected     = SelectTestProjects(solution, projectFilter, out var selectionError);

        if (selectionError is not null)
            return new { error = selectionError, testProjectsDetected = testProjects.Select(project => project.Name).ToList() };

        // Without a filter the solution runs once and dotnet test finds its own test projects; with one,
        // each matching project runs on its own — that is what makes the filter select a project.
        var targets = projectFilter is null && solutionPath is not null
            ? new List<string> { solutionPath }
            : selected.Select(project => project.FilePath!).ToList();

        if (targets.Count == 0)
            return new
            {
                error = "Nothing to run: the solution has no path on disk and no test project was detected.",
                testProjectsDetected = testProjects.Select(project => project.Name).ToList(),
            };

        var tmpDir = Path.Combine(Path.GetTempPath(), $"mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var runs = new List<object>();
            var outputs = new List<string>();
            var notRun = new List<string>();
            long durationMs = 0;
            var succeeded = true;
            var timedOut = false;

            // One deadline for the whole call, not one per target: a projectFilter that selects three
            // projects must not hold the client for three times the budget.
            var budget = Math.Clamp(timeoutSeconds, 10, 3600);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(budget));

            var next = 0;
            for (; next < targets.Count; next++)
            {
                if (deadline.IsCancellationRequested) break;

                // One results folder per target, so two projects cannot overwrite each other's TRX.
                var resultsDir = Path.Combine(tmpDir, $"run-{next}");
                Directory.CreateDirectory(resultsDir);

                var args = BuildArgs(targets[next], resultsDir, extraArgs);
                var (exitCode, stdout, stderr, elapsed, killed) = await ExecAsync("dotnet", args, deadline.Token);

                durationMs += (long)elapsed.TotalMilliseconds;
                timedOut   |= killed;
                succeeded  &= !killed && exitCode == 0;

                outputs.Add($"$ dotnet {args}"
                            + Environment.NewLine + stdout
                            + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + stderr)
                            + (killed ? Environment.NewLine + $"(timed out after {budget}s; the process tree was killed)" : string.Empty));

                runs.Add(new
                {
                    target = targets[next],
                    exitCode,
                    durationMs = (long)elapsed.TotalMilliseconds,
                    timedOut = killed,
                });

                if (!killed) continue;

                next++;
                break;
            }

            // The targets after the one that used the last of the budget are named instead of silently dropped,
            // and a budget that expired between two targets is still a timeout.
            if (next < targets.Count) timedOut = true;
            for (; next < targets.Count; next++) notRun.Add(targets[next]);

            // Parse every .trx produced under tmpDir
            var trxFiles       = Directory.GetFiles(tmpDir, "*.trx", SearchOption.AllDirectories);
            var projectResults = trxFiles.Select(ParseTrx).Where(result => result is not null).Select(result => result!).ToList();

            var totalPassed  = projectResults.Sum(r => r.Passed);
            var totalFailed  = projectResults.Sum(r => r.Failed);
            var totalSkipped = projectResults.Sum(r => r.Skipped);
            var totalTests   = projectResults.Sum(r => r.Total);

            var recordPath = TestRunRecordStore.PathFor(SolutionDirectory(solution, solutionPath));
            if (trxFiles.Length > 0)
            {
                TestRunRecordStore.Write(
                    SolutionDirectory(solution, solutionPath),
                    projectResults
                        .Where(result => result.Failed > 0)
                        .Select(result => new FailedTestGroup(
                            result.ProjectName,
                            result.FailedTests
                                .Select(failure => failure.TestName)
                                .Where(name => !string.IsNullOrWhiteSpace(name))
                                .Select(name => name!)
                                .ToList()))
                        .ToList());
            }

            return new
            {
                solutionPath,
                testProjectsDetected = testProjects.Select(project => project.Name).ToList(),
                targets,
                timeoutSeconds = budget,
                timedOut,
                notRun,
                summary = new
                {
                    total    = totalTests,
                    passed   = totalPassed,
                    failed   = totalFailed,
                    skipped  = totalSkipped,
                    outcome  = timedOut ? "TimedOut" : succeeded ? "Passed" : "Failed",
                    durationMs,
                },
                projectResults,
                runs,
                record = recordPath,
                note = timedOut
                    ? $"The run was stopped after {budget}s and the 'dotnet test' process tree was killed. " +
                      (notRun.Count > 0 ? $"{notRun.Count} target(s) did not run. " : string.Empty) +
                      "Narrow the run (run_test, run_affected_tests) or raise timeoutSeconds."
                    : totalFailed > 0
                        ? "The failed tests are recorded: rerun_failed re-runs exactly them."
                        : "No test failed, so the record of the last run is empty.",
                // The raw output is what explains a run that produced no TRX at all (a build error, a bad filter, a timeout).
                rawOutput = trxFiles.Length == 0 ? outputs : null,
            };
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The test projects of the solution: a <c>.csproj</c> that references <c>Microsoft.NET.Test.Sdk</c>.
    /// </summary>
    public static List<Project> DetectTestProjects(Solution solution) =>
        solution.Projects
            .Where(IsTestProject)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The projects a <c>projectFilter</c> selects: every test project whose name or file name contains
    /// it (case-insensitive), or all of them when no filter is given. A filter that matches nothing is an
    /// error naming the projects that do exist — never a silent run of the whole solution.
    /// </summary>
    public static IReadOnlyList<Project> SelectTestProjects(Solution solution, string? projectFilter, out string? error)
    {
        error = null;
        var testProjects = DetectTestProjects(solution);
        if (string.IsNullOrWhiteSpace(projectFilter)) return testProjects;

        var filter  = projectFilter.Trim();
        var matched = testProjects
            .Where(project => project.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              (project.FilePath is not null &&
                               Path.GetFileName(project.FilePath).Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matched.Count == 0)
        {
            error = testProjects.Count == 0
                ? $"No test project matches '{filter}': the solution declares none (a project referencing Microsoft.NET.Test.Sdk)."
                : $"No test project matches '{filter}'. Test projects: {string.Join(", ", testProjects.Select(project => project.Name))}.";
            return [];
        }

        return matched;
    }

    /// <summary>The filter a recorded failure re-runs: an exact name when it is qualified, a fragment otherwise.</summary>
    public static string ComposeFailureFilter(IEnumerable<string> failedTests) =>
        string.Join("|", failedTests.Select(name =>
            name.Contains('.') ? $"FullyQualifiedName={name}" : $"FullyQualifiedName~{name}"));

    // -- helpers -------------------------------------------------------------

    private static bool IsTestProject(Project project)
    {
        if (project.FilePath is null || !File.Exists(project.FilePath)) return false;

        try
        {
            return File.ReadAllText(project.FilePath)
                .Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? SolutionDirectory(Solution solution, string? solutionPath)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath)) return Path.GetDirectoryName(solutionPath);

        return solution.Projects
            .Select(project => project.FilePath)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(file => Path.GetDirectoryName(file!))
            .FirstOrDefault();
    }

    /// <summary>
    /// The <c>dotnet test</c> command for one target: the project filter selects the target, so it never
    /// becomes a <c>--filter</c>; extra arguments are appended as written.
    /// </summary>
    public static string BuildArgs(string target, string resultsDir, string? extra)
    {
        var parts = new List<string>
        {
            "test",
            $"\"{target}\"",
            $"--logger \"trx\"",
            $"--results-directory \"{resultsDir}\"",
        };
        if (!string.IsNullOrWhiteSpace(extra))
            parts.Add(extra);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// One <c>dotnet test</c> target, bounded by the caller's deadline. The stream reads are started without
    /// the token so the output written before the timeout survives the kill: a killed process closes its
    /// pipes, and what it printed on the way there is the only thing that explains the run.
    /// </summary>
    private static async Task<(int exitCode, string stdout, string stderr, TimeSpan elapsed, bool timedOut)> ExecAsync(
        string exe, string args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        Snitch.Execution.ProcessRunner.RemoveInheritedMsBuildEnv(psi);

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        sw.Stop();

        return (timedOut ? -1 : proc.ExitCode, await Drain(stdoutTask), await Drain(stderrTask), sw.Elapsed, timedOut);
    }

    /// <summary>The output of a killed process, without letting a survivor hold the call.</summary>
    private static async Task<string> Drain(Task<string> stream)
    {
        try { return await stream.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { return string.Empty; }
    }

    private static TrxProjectResult? ParseTrx(string trxPath)
    {
        try
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var doc = XDocument.Load(trxPath);

            // Project name from the TestRun name attribute ("projectName @ timestamp")
            var runName     = doc.Root?.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(trxPath);
            var projectName = runName.Contains('@') ? runName[..runName.LastIndexOf('@')].Trim() : runName;

            // Counters
            var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
            int total    = Attr(counters, "total");
            int passed   = Attr(counters, "passed");
            int failed   = Attr(counters, "failed");
            int skipped  = Attr(counters, "notExecuted") + Attr(counters, "disconnected");

            // Failed test details
            var failures = doc.Descendants(ns + "UnitTestResult")
                .Where(e => e.Attribute("outcome")?.Value is "Failed" or "Error")
                .Select(e =>
                {
                    var output = e.Element(ns + "Output");
                    var msg    = output?.Element(ns + "ErrorInfo")?.Element(ns + "Message")?.Value?.Trim();
                    var trace  = output?.Element(ns + "ErrorInfo")?.Element(ns + "StackTrace")?.Value?.Trim();
                    return new TestFailure(
                        e.Attribute("testName")?.Value,
                        e.Attribute("duration")?.Value,
                        msg,
                        trace);
                })
                .ToList();

            return new TrxProjectResult(projectName, total, passed, failed, skipped, failures);

            int Attr(XElement? el, string attr) =>
                el is null ? 0 : int.TryParse(el.Attribute(attr)?.Value, out var v) ? v : 0;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record TestFailure(string? TestName, string? Duration, string? Message, string? StackTrace);

internal record TrxProjectResult(
    string ProjectName,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestFailure> FailedTests);
