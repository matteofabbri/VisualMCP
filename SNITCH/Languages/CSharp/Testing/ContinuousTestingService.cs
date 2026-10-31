using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// Continuous testing without an IDE: a loop that notices a source document changing on disk and runs the
/// tests (all, or the filter you give) after a short debounce, keeping the last result for a status call.
/// The change signal is the file's write time — an editor save counts, not only a tool write.
/// </summary>
public interface IContinuousTestingService
{
    Task<object> StartAsync(int intervalSeconds, string? projectName, string? testFilter, CancellationToken cancellationToken = default);
    Task<object> StopAsync(CancellationToken cancellationToken = default);
    object Status();
    Task<object> RunNowAsync(string? projectName, string? testFilter, int timeoutSeconds, CancellationToken cancellationToken = default);
}

public sealed class ContinuousTestingService : IContinuousTestingService
{
    private readonly IRoslynWorkspace _workspace;

    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private volatile bool _running;
    private int _intervalSeconds = 10;
    private int _runs;
    private long _lastRunTicks;
    private long _lastRunMs;
    private int _lastPassed;
    private int _lastFailed;
    private string? _lastError;
    private string[] _lastFailures = [];
    private int _watchedFiles;

    public ContinuousTestingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public Task<object> StartAsync(
        int intervalSeconds, string? projectName, string? testFilter, CancellationToken cancellationToken = default)
    {
        if (_running)
            return Task.FromResult<object>(new { running = true, intervalSeconds = _intervalSeconds, note = "Already running." });

        var interval = Math.Clamp(intervalSeconds < 2 ? 10 : intervalSeconds, 2, 600);
        _intervalSeconds = interval;
        _loop = new CancellationTokenSource();
        _running = true;
        _loopTask = Task.Run(() => LoopAsync(interval, projectName, testFilter, _loop.Token), CancellationToken.None);

        return Task.FromResult<object>(new
        {
            running = true,
            intervalSeconds = interval,
            projectName,
            filter = testFilter ?? "all tests",
            note = "Watching the solution's source files: after a file changes and a short debounce, the tests run once and " +
                   "the result is kept for status. Poll continuous_testing action=status; stop it before a long batch of edits " +
                   "if running after each one is not what you want.",
        });
    }

    public async Task<object> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is null) return new { running = false, note = "Not running." };

        try { _loop.Cancel(); } catch { /* already cancelled */ }
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (Exception) { /* the loop ends on cancellation */ }
        }

        _loop.Dispose();
        _loop = null;
        _loopTask = null;
        _running = false;

        return new { running = false, runs = _runs, note = "Continuous testing stopped; the last result is still in status." };
    }

    public object Status()
    {
        return new
        {
            running = _running,
            intervalSeconds = _intervalSeconds,
            watchedFiles = _watchedFiles,
            runs = _runs,
            lastRunUtc = _lastRunTicks == 0 ? null : new DateTimeOffset(_lastRunTicks, TimeSpan.Zero).ToString("u"),
            lastRunMs = _lastRunMs,
            lastPassed = _lastPassed,
            lastFailed = _lastFailed,
            lastFailures = _lastFailures,
            lastError = _lastError,
            note = _running
                ? "A change in a source document triggers one run after a short debounce; status is cheap to poll."
                : "Not running: start it, or use run-now for a single run.",
        };
    }

    public async Task<object> RunNowAsync(
        string? projectName, string? testFilter, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-loaded from the working directory." };

        var project = TestSuiteRunner.ResolveTestProject(solution, projectName, out var resolveError);
        if (project is null) return new { error = resolveError };

        return await RunOnceAsync(project.Name, project.FilePath!, testFilter, timeoutSeconds, cancellationToken);
    }

    private async Task LoopAsync(int intervalSeconds, string? projectName, string? testFilter, CancellationToken cancellationToken)
    {
        Dictionary<string, long>? snapshot = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);

                var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
                if (solution is null) continue;

                var current = Snapshot(solution);
                _watchedFiles = current.Count;

                if (snapshot is null)
                {
                    snapshot = current;
                    continue;
                }

                if (SameSnapshot(snapshot, current))
                {
                    snapshot = current;
                    continue;
                }

                // A save often comes with a build writing several files: one run, after the dust settles.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                snapshot = Snapshot(solution);

                var project = TestSuiteRunner.ResolveTestProject(solution, projectName, out var error);
                if (project is null)
                {
                    _lastError = error;
                    continue;
                }

                await RunOnceAsync(project.Name, project.FilePath!, testFilter, 600, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        finally
        {
            _running = false;
        }
    }

    private async Task<object> RunOnceAsync(
        string projectName, string projectPath, string? filter, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await TestSuiteRunner.RunAsync(projectPath, filter, timeoutSeconds, cancellationToken);
        stopwatch.Stop();

        Interlocked.Increment(ref _runs);
        _lastRunMs = stopwatch.ElapsedMilliseconds;
        _lastError = result.Error;
        _lastPassed = result.Passed;
        _lastFailed = result.Failed;
        _lastFailures = result.Outcomes
            .Where(pair => !SuiteResult.IsPassed(pair.Value) && !pair.Value.Equals("Skipped", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .Take(20)
            .ToArray();
        Interlocked.Exchange(ref _lastRunTicks, DateTimeOffset.UtcNow.UtcTicks);

        return new
        {
            ran = result.Ran,
            project = projectName,
            filter = filter ?? "all tests",
            passed = result.Passed,
            failed = result.Failed,
            skipped = result.Skipped,
            tests = result.Outcomes.Count,
            failures = _lastFailures,
            elapsedMs = _lastRunMs,
            running = _running,
            error = result.Error,
            note = result.Ran
                ? "One run; the same shape run_tests reports for a filter."
                : "The run produced no TRX result: check the filter and the project's test framework.",
        };
    }

    private static Dictionary<string, long> Snapshot(Microsoft.CodeAnalysis.Solution solution)
    {
        var snapshot = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in solution.Projects
                     .SelectMany(project => project.Documents)
                     .Where(document => document.SourceCodeKind == Microsoft.CodeAnalysis.SourceCodeKind.Regular))
        {
            if (document.FilePath is null) continue;
            try
            {
                if (!File.Exists(document.FilePath)) continue;
                snapshot[document.FilePath] = File.GetLastWriteTimeUtc(document.FilePath).Ticks;
            }
            catch (IOException)
            {
                // A file being written is simply not part of this snapshot.
            }
        }

        return snapshot;
    }

    private static bool SameSnapshot(IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (path, ticks) in left)
        {
            if (!right.TryGetValue(path, out var other) || other != ticks) return false;
        }

        return true;
    }
}
