using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>What the warm analysis knows right now.</summary>
public sealed record BackgroundAnalysisStatus(
    bool Running,
    int IntervalSeconds,
    string? LastRunUtc,
    long LastRunMs,
    int Findings,
    string? LastError,
    string? InspectedSolution);

/// <summary>
/// The warm half of the inspection surface: a loop that re-runs the inspection packs when the loaded
/// solution changes, so a client that polls <c>analysis_findings</c> gets the current findings between
/// calls instead of paying for a full inspection each time. The findings are cached per file and per
/// inspection id; <c>inspect_code</c> itself is unchanged and still answers one-shot questions.
/// </summary>
public interface IBackgroundAnalysisService
{
    Task<object> StartAsync(int intervalSeconds, CancellationToken cancellationToken = default);
    Task<object> StopAsync(CancellationToken cancellationToken = default);
    Task<object> RefreshAsync(CancellationToken cancellationToken = default);
    object Status();

    /// <summary>The findings of the last run, optionally narrowed by file, project, severity or id.</summary>
    object Findings(string? filePath, string? projectName, string? minSeverity, int maxResults);
}

public sealed class BackgroundAnalysisService : IBackgroundAnalysisService
{
    private const string DefaultPacks = "house,roslynator,netanalyzers,asyncfixer";

    private readonly IRoslynWorkspace _workspace;
    private readonly IInspectCodeService _inspections;

    private readonly ConcurrentDictionary<string, IReadOnlyList<InspectionFinding>> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _byInspection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _bySeverity = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private volatile bool _running;
    private int _intervalSeconds = 60;
    private long _lastRunTicks;
    private long _lastRunMs;
    private int _findings;
    private string? _lastError;
    private string? _inspectedSolution;
    private Solution? _lastSolution;

    public BackgroundAnalysisService(IRoslynWorkspace workspace, IInspectCodeService inspections)
    {
        _workspace = workspace;
        _inspections = inspections;
    }

    public Task<object> StartAsync(int intervalSeconds, CancellationToken cancellationToken = default)
    {
        var interval = Math.Clamp(intervalSeconds < 5 ? 60 : intervalSeconds, 5, 3600);

        if (_running)
            return Task.FromResult<object>(new { running = true, intervalSeconds = _intervalSeconds, note = "Already running." });

        _intervalSeconds = interval;
        _loop = new CancellationTokenSource();
        _running = true;

        _loopTask = Task.Run(() => LoopAsync(interval, _loop.Token), CancellationToken.None);

        return Task.FromResult<object>(new
        {
            running = true,
            intervalSeconds = interval,
            note = "Background analysis started: the inspection packs run when the solution changes (and immediately " +
                   "for the solution that is loaded now). Poll analysis_findings for the cached result; stop it with " +
                   "action=stop when a long batch of edits would otherwise re-inspect between them.",
        });
    }

    public async Task<object> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is null)
            return new { running = false, note = "Not running." };

        try { _loop.Cancel(); } catch { /* already disposed */ }

        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (Exception) { /* the loop ends on its own cancellation */ }
        }

        _loop.Dispose();
        _loop = null;
        _loopTask = null;
        _running = false;

        return new { running = false, note = "Background analysis stopped; the last findings are still available." };
    }

    public async Task<object> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory." };

        return await AnalyzeAsync(solution, cancellationToken);
    }

    public object Status()
    {
        var status = new BackgroundAnalysisStatus(
            _running,
            _intervalSeconds,
            _lastRunTicks == 0 ? null : new DateTimeOffset(_lastRunTicks, TimeSpan.Zero).ToString("u"),
            _lastRunMs,
            _findings,
            _lastError,
            _inspectedSolution);

        return new
        {
            running = status.Running,
            intervalSeconds = status.IntervalSeconds,
            lastRunUtc = status.LastRunUtc,
            lastRunMs = status.LastRunMs,
            findings = status.Findings,
            lastError = status.LastError,
            inspectedSolution = status.InspectedSolution,
            note = status.Running
                ? "The loop re-runs the packs when the loaded Solution instance changes. analysis_findings answers from the cache."
                : "Not running: call background_analysis with action=start (or refresh for a single warm run).",
        };
    }

    public object Findings(string? filePath, string? projectName, string? minSeverity, int maxResults)
    {
        var minimum = (minSeverity ?? "info").Trim().ToLowerInvariant() switch
        {
            "error" => 3,
            "warning" => 2,
            _ => 1,
        };

        var requestedFile = filePath is null ? null : Path.GetFullPath(filePath);
        var query = _byFile.Values.SelectMany(findings => findings)
            .Where(finding => requestedFile is null ||
                              (finding.File.Length > 0 && string.Equals(Path.GetFullPath(finding.File), requestedFile, StringComparison.OrdinalIgnoreCase)))
            .Where(finding => projectName is null || finding.Project.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .Where(finding => Rank(finding.Severity) >= minimum)
            .OrderBy(finding => finding.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Line)
            .ToList();

        var limit = Math.Clamp(maxResults < 1 ? 100 : maxResults, 1, 5000);
        return new
        {
            cached = _lastRunTicks != 0,
            lastRunUtc = _lastRunTicks == 0 ? null : new DateTimeOffset(_lastRunTicks, TimeSpan.Zero).ToString("u"),
            total = query.Count,
            bySeverity = _bySeverity.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            byInspection = _byInspection.OrderByDescending(pair => pair.Value).Take(15).ToDictionary(pair => pair.Key, pair => pair.Value),
            findings = query.Take(limit).Select(finding => new
            {
                inspection = finding.InspectionId,
                severity = finding.Severity,
                message = finding.Message,
                project = finding.Project,
                file = finding.File,
                finding.Line,
                finding.Column,
            }).ToList(),
            truncated = query.Count > limit,
            note = _running
                ? "Served from the warm cache; it refreshes on its own when the solution changes."
                : "Served from the last run; call background_analysis with action=refresh for a fresh one.",
        };
    }

    private async Task LoopAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
                if (solution is not null && !ReferenceEquals(solution, _lastSolution))
                    await AnalyzeAsync(solution, cancellationToken);

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
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

    private async Task<object> AnalyzeAsync(Solution solution, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var report = await _inspections.RunAsync(
                null, null, DefaultPacks, "info", null, null, enableAll: true, maxResults: 5000, timeoutSeconds: 300);

            _byFile.Clear();
            _byInspection.Clear();
            _bySeverity.Clear();

            foreach (var finding in report.Findings)
            {
                var key = finding.File.Length == 0 ? "(no file)" : finding.File;
                _byFile.AddOrUpdate(key, _ => [finding], (_, existing) => [.. existing, finding]);
                _byInspection.AddOrUpdate(finding.InspectionId, 1, (_, count) => count + 1);
                _bySeverity.AddOrUpdate(finding.Severity, 1, (_, count) => count + 1);
            }

            _findings = report.Total;
            _lastError = report.Error;
            _lastSolution = solution;
            _inspectedSolution = report.Solution;
            _lastRunMs = stopwatch.ElapsedMilliseconds;
            Interlocked.Exchange(ref _lastRunTicks, DateTimeOffset.UtcNow.UtcTicks);

            return new
            {
                ran = true,
                solution = report.Solution,
                findings = report.Total,
                bySeverity = _bySeverity.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                elapsedMs = _lastRunMs,
                running = _running,
                note = "Cached: analysis_findings answers from this run. inspect_code still runs a fresh, filtered inspection.",
            };
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _lastRunMs = stopwatch.ElapsedMilliseconds;
            return new { ran = false, error = exception.Message, elapsedMs = _lastRunMs };
        }
    }

    private static int Rank(string severity) => severity.Trim().ToLowerInvariant() switch
    {
        "error" => 3,
        "warning" => 2,
        _ => 1,
    };
}
