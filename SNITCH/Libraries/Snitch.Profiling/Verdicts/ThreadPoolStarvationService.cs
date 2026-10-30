using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace Snitch.Profiling.Verdicts;

public interface IThreadPoolStarvationService
{
    /// <summary>
    /// Answers "is the thread pool starved?" against a live process: the thread-pool counters, a
    /// runtime trace (queue, contention, blocked intervals) and the live stacks.
    /// </summary>
    Task<ThreadPoolReport> AnalyzeProcessAsync(
        int processId,
        string processName,
        int durationSeconds,
        string outputDirectory,
        int top = 15,
        bool resolveSource = true,
        string? userCodeRoot = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the same question from measurements that already exist. Any of counters, stacks,
    /// trace and I/O report may be null; the answer states which signals it had.
    /// </summary>
    Task<ThreadPoolReport> AnalyzeAsync(
        CounterCollectionResult? counters,
        StackSnapshot? stacks,
        TraceAnalysis? trace = null,
        IoBlockReport? io = null,
        int top = 15,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The "is the thread pool starved" verdict. A full queue is not starvation by itself: a burst that
/// drains while the pool grows its worker count is healthy, and a queue of zero with twelve threads
/// parked on Task.Result is starving. This service separates the two by requiring both halves of
/// the signature — work waiting <em>and</em> threads that cannot take it — and it names the blocking
/// site, because "make it async" is only actionable with a file and a line.
///
/// States: <c>starved</c>, <c>at-risk</c>, <c>burst</c> (a spike that drained), <c>healthy</c>,
/// <c>inconclusive</c> (not enough signals to answer).
/// </summary>
public sealed class ThreadPoolStarvationService : IThreadPoolStarvationService
{
    private readonly ICounterCollectionService _counters;
    private readonly IManagedStackService _stacks;
    private readonly ITraceCollectionService _traces;
    private readonly ITraceAnalysisService _analysis;
    private readonly IIoBlockService _io;
    private readonly IProfilingAdvisor _advisor;
    private readonly ProfilingOptions _options;

    public ThreadPoolStarvationService(
        ICounterCollectionService counters,
        IManagedStackService stacks,
        ITraceCollectionService traces,
        ITraceAnalysisService analysis,
        IIoBlockService io,
        IProfilingAdvisor advisor,
        ProfilingOptions options)
    {
        _counters = counters;
        _stacks = stacks;
        _traces = traces;
        _analysis = analysis;
        _io = io;
        _advisor = advisor;
        _options = options;
    }

    public async Task<ThreadPoolReport> AnalyzeProcessAsync(
        int processId,
        string processName,
        int durationSeconds,
        string outputDirectory,
        int top = 15,
        bool resolveSource = true,
        string? userCodeRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            throw new ArgumentException("A positive process id is required.", nameof(processId));
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("An output directory is required.", nameof(outputDirectory));

        var duration = Math.Clamp(
            durationSeconds <= 0 ? _options.DefaultDurationSeconds : durationSeconds,
            3, _options.MaxDurationSeconds);
        var warnings = new List<string>();

        CounterCollectionResult? counters = null;
        try
        {
            counters = await _counters.CollectAsync(
                processId,
                ["threadpool-queue-length", "threadpool-thread-count", "cpu-usage", "monitor-lock-contention-count"],
                duration, intervalSeconds: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"The counter session failed, so the queue numbers come from the trace only: {ex.Message}");
        }

        var safeName = string.Concat(processName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        Directory.CreateDirectory(outputDirectory);
        var tracePath = Path.Combine(
            outputDirectory, $"pool-{safeName}-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.nettrace");

        // The runtime preset carries the sample profiler: without it there are no External samples
        // and therefore no blocked intervals, which are half of the starvation signature.
        var collected = await _traces.CollectAsync(
            processId, tracePath, "runtime", customProviders: null, duration,
            bufferMb: 256, requestStackwalk: true, cancellationToken).ConfigureAwait(false);

        StackSnapshot? stacks = null;
        try
        {
            stacks = await _stacks.CaptureAsync(processId, maxFrames: 60, includeRuntimeFrames: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"The stack snapshot failed, so the blocking threads are seen through the trace only: {ex.Message}");
        }

        var trace = await _analysis.AnalyzeAsync(
            collected.Path, top, resolveSource, deep: true, cancellationToken).ConfigureAwait(false);
        var io = await _io.AnalyzeAsync(
            collected.Path, top, minPercent: 1.0, resolveSource,
            userCodeRoot ?? outputDirectory, cancellationToken).ConfigureAwait(false);

        var report = await AnalyzeAsync(counters, stacks, trace, io, top, cancellationToken).ConfigureAwait(false);
        return report with
        {
            Answer = report.Answer with
            {
                Warnings = report.Answer.Warnings
                    .Concat(warnings)
                    .Concat(trace.Warnings)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            },
        };
    }

    public Task<ThreadPoolReport> AnalyzeAsync(
        CounterCollectionResult? counters,
        StackSnapshot? stacks,
        TraceAnalysis? trace = null,
        IoBlockReport? io = null,
        int top = 15,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        top = Math.Clamp(top <= 0 ? 15 : top, 1, 100);

        var warnings = new List<string>();
        if (counters is not null) warnings.AddRange(counters.Warnings);
        if (trace is not null) warnings.AddRange(trace.Warnings);
        if (io is not null) warnings.AddRange(io.Warnings);

        var queue = Find(counters, "threadpool-queue-length");
        var workers = Find(counters, "threadpool-thread-count");
        var blockedThreads = stacks is null
            ? 0
            : stacks.Threads.Count(t => t.Frames.Count > 0 && AnswerBuilder.IsBlockingFrame(t.Frames[0].Method));
        var totalThreads = stacks?.Threads.Count ?? 0;

        var sync = io is null ? null : io.Categories.FirstOrDefault(c => c.Category == "SyncOverAsync");
        var syncSites = (io?.Sites ?? Array.Empty<IoBlockSite>())
            .Where(s => s.Category == "SyncOverAsync" || s.Stack.Any(AnswerBuilder.IsThreadPoolFrame))
            .Take(top)
            .ToList();
        var syncMs = sync?.BlockedMs ?? 0;
        var backlog = trace?.ThreadPool.MaxBacklog;

        var peak = queue?.Max;
        var last = queue?.Last;
        var growth = queue is null
            ? (double?)null
            : queue.Last - (queue.Samples.Count > 0 ? queue.Samples[0].Value : queue.Min);
        var drained = queue is not null && queue.Max > 0 && queue.Last <= Math.Max(50, queue.Max * 0.25);

        var prolonged = queue is null ? backlog is not null : !drained;
        var backPressure = blockedThreads >= 4 || syncMs >= 500 || syncSites.Count > 0;
        var queueHigh = peak >= 1000 || (backlog ?? 0) >= 1000;

        string state;
        if (queue is null && backlog is null && stacks is null && io is null)
            state = "inconclusive";
        else if (queueHigh && prolonged && backPressure)
            state = "starved";
        else if (peak >= 1000 && drained)
            state = "burst";
        else if (backPressure && ((peak ?? 0) >= 200 || (backlog ?? 0) >= 200))
            state = "at-risk";
        else if (blockedThreads >= 8)
            state = "at-risk";
        else if (queue is not null || backlog is not null)
            state = "healthy";
        else
            state = "inconclusive";

        var queueWeight = Math.Min(1.0, (peak ?? (double)(backlog ?? 0)) / 1000.0);
        var syncWeight = sync is null
            ? 0
            : Math.Min(1.0, Math.Max(sync.BlockedMs / Math.Max(1, io!.DurationMs),
                blockedThreads / (double)Math.Max(1, totalThreads)));
        var verdictWeight = state switch
        {
            "starved" => Math.Max(queueWeight, syncWeight),
            "at-risk" => Math.Max(queueWeight * 0.6, syncWeight),
            "burst" => queueWeight,
            _ => queueWeight,
        };

        var (answer, verdictFinding) = Describe(
            state, queue, peak, last, growth, workers, blockedThreads, totalThreads, sync, syncSites, syncMs, backlog, drained);

        var evidence = new List<AnswerEvidence>
        {
            new(1, Math.Round(verdictWeight, 4), verdictFinding.Id, verdictFinding.Severity,
                verdictFinding.Title, verdictFinding.Evidence, verdictFinding.Recommendation,
                verdictFinding.SuggestedAction, verdictFinding.Source),
        };

        var supporting = new List<WeightedFinding>();
        if (queue is not null)
        {
            supporting.Add(new WeightedFinding(queueWeight, new ProfilingFinding(
                "threadpool-queue-length", queue.Max >= 1000 ? "warning" : "info",
                "Thread-pool queue over the sampled window",
                $"peak {queue.Max:N0}, mean {queue.Mean:N0}, last {queue.Last:N0} over {queue.Points} sample(s)" +
                (growth is null ? "." : $"; changed by {growth:+0;-0;0} from the first sample."),
                queue.Max >= 1000
                    ? "A queue that stays high while threads are blocked is starvation; remove the blocking call."
                    : "The queue was not the bottleneck in this window.",
                null,
                null)));
        }
        if (io is not null)
            supporting.AddRange(io.Findings.Select(f => new WeightedFinding(
                f.Id is "sync-over-async" or "thread-pool-blocking" or "io-pressure" ? syncWeight : 0.0, f)));
        if (stacks is not null)
            supporting.AddRange(_advisor.Analyze(stacks).Select(f => new WeightedFinding(
                f.Id switch
                {
                    "blocked-threads" => blockedThreads / (double)Math.Max(1, totalThreads),
                    "monitor-contention" => syncWeight * 0.5,
                    _ => 0.05,
                },
                f)));
        if (trace is not null)
            supporting.AddRange(_advisor.Analyze(trace).Select(f => new WeightedFinding(
                f.Id is "threadpool-starvation" or "lock-contention" ? Math.Max(queueWeight, syncWeight) : 0.0, f)));
        if (counters is not null)
            supporting.AddRange(_advisor.Analyze(counters).Select(f => new WeightedFinding(
                f.Id is "threadpool-queue" or "cpu-saturated" ? queueWeight : 0.0, f)));

        evidence.AddRange(AnswerBuilder.Rank(
            supporting.Where(s => s.Weight > 0 && s.Finding.Id != verdictFinding.Id), startRank: 2));

        var extraSteps = state switch
        {
            "healthy" => new[]
            {
                "Nothing to do for the pool: if latency is still high, run the slowdown analysis to see where time goes instead.",
            },
            "burst" => new[]
            {
                "No change needed: the queue drained. Re-run this analysis during the slow window if the burst is the symptom.",
            },
            "inconclusive" => new[]
            {
                "Collect counters (profile_counters) and stacks (profile_stacks) together, or call the live overload of this analysis.",
            },
            _ => Array.Empty<string>(),
        };

        return Task.FromResult(new ThreadPoolReport(
            new AnswerReport(
                "Is the thread pool starved?",
                state,
                answer,
                Confidence(stacks, trace, queue),
                evidence,
                AnswerBuilder.NextSteps(evidence, extraSteps),
                Measurements(queue, workers, blockedThreads, totalThreads, sync, backlog, trace),
                warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            state,
            peak,
            last,
            growth,
            workers?.Min,
            workers?.Max,
            blockedThreads,
            totalThreads,
            backlog,
            syncSites,
            counters,
            stacks,
            trace,
            io));
    }

    private static (string Answer, ProfilingFinding Finding) Describe(
        string state,
        CounterSeries? queue,
        double? peak,
        double? last,
        double? growth,
        CounterSeries? workers,
        int blockedThreads,
        int totalThreads,
        IoCategorySummary? sync,
        IReadOnlyList<IoBlockSite> syncSites,
        double syncMs,
        long? backlog,
        bool drained)
    {
        var site = syncSites.FirstOrDefault();
        var siteText = site is null
            ? ""
            : $", at {AnswerBuilder.ShortName(site.CallSite)}" + (site.Source is null ? "" : $" ({site.Source})");

        switch (state)
        {
            case "starved":
            {
                var answer =
                    $"The thread pool is starved: the queue peaked at {peak ?? 0:N0}" +
                    (backlog is > 0 ? $" (trace backlog {backlog:N0})" : "") +
                    $" while {blockedThreads} thread(s) are parked in synchronous waits" +
                    (sync is null ? "" : $"; {syncMs:N0} ms of that waits on work that is already asynchronous") +
                    siteText + ".";
                return (answer, new ProfilingFinding(
                    "pool-starvation", "critical",
                    "The thread pool is starved",
                    $"queue peak {peak ?? 0:N0}, last {last ?? 0:N0}" +
                    (backlog is null ? "" : $", trace backlog {backlog:N0}") +
                    $"; {blockedThreads} of {totalThreads} thread(s) blocked" +
                    (sync is null ? "" : $"; sync-over-async {syncMs:N0} ms across {sync.Sites} site(s)"),
                    "Remove the blocking call from the pool thread: await the work instead of Task.Wait/.Result/GetAwaiter().GetResult().",
                    site?.Source is null ? "profile_stacks to read the blocked stacks." : $"Open {site.Source} and convert the wait to await.",
                    site?.Source));
            }

            case "burst":
            {
                var answer =
                    $"A queue burst, not starvation: the queue peaked at {peak ?? 0:N0} but drained to {last ?? 0:N0} " +
                    $"on {workers?.Max ?? 0:N0} worker thread(s), with {blockedThreads} thread(s) blocked.";
                return (answer, new ProfilingFinding(
                    "pool-burst", "info",
                    "The queue spiked and drained",
                    $"queue {peak ?? 0:N0} → {last ?? 0:N0}, workers {workers?.Min ?? 0:N0} → {workers?.Max ?? 0:N0}, " +
                    $"{blockedThreads} blocked thread(s).",
                    "The pool kept up. If the spike caused latency, the work enqueued then is the unit to profile.",
                    "profile_collect during the burst, then profile_hotpaths on the trace.",
                    null));
            }

            case "at-risk":
            {
                var signals = new List<string>();
                if (peak is not null) signals.Add($"queue peak {peak:N0}");
                if (backlog is not null) signals.Add($"trace backlog {backlog:N0}");
                signals.Add($"{blockedThreads} blocked thread(s)");
                if (sync is not null) signals.Add($"{syncMs:N0} ms sync-over-async");

                var answer =
                    $"The pool is at risk: {string.Join(", ", signals)}" + siteText + " — the wait is real but has not yet " +
                    "produced the full queue + blocked-thread signature of starvation.";
                return (answer, new ProfilingFinding(
                    "pool-at-risk", "warning",
                    "The pool shows the beginning of starvation",
                    string.Join("; ", signals) + (growth is null ? "." : $"; queue changed by {growth:+0;-0;0} over the window."),
                    "Act on the blocking site before the queue grows: convert it to await.",
                    site?.Source is null ? "profile_stacks to read the blocked stacks." : $"Open {site.Source}.",
                    site?.Source));
            }

            case "inconclusive":
            {
                var answer =
                    "Cannot tell whether the pool is starved: " +
                    (queue is null && backlog is null
                        ? "there is no thread-pool queue measurement (counters or trace) in the input"
                        : "the available signals do not cover the pool") + ".";
                return (answer, new ProfilingFinding(
                    "pool-inconclusive", "info",
                    "No thread-pool queue signal",
                    "Neither the counters nor the trace carried threadpool-queue-length / backlog data.",
                    "Collect the thread-pool counters and the stacks in the same call.",
                    "profile_counters countersFilter='threadpool-queue-length,threadpool-thread-count' plus profile_stacks.",
                    null));
            }

            default:
            {
                var answer =
                    $"The thread pool is not starved: the queue peaked at {peak ?? 0:N0} and drained to {last ?? 0:N0} " +
                    $"with {blockedThreads} of {totalThreads} thread(s) blocked" +
                    (drained ? " (the queue fell back to a quarter of its peak or below)." : ".");
                return (answer, new ProfilingFinding(
                    "pool-healthy", "info",
                    "The thread pool is keeping up",
                    $"queue peak {peak ?? 0:N0}, mean {queue?.Mean ?? 0:N0}, last {last ?? 0:N0}; " +
                    $"workers {workers?.Min ?? 0:N0} → {workers?.Max ?? 0:N0}; {blockedThreads} blocked thread(s).",
                    "No pool change needed. If latency is still high, the cost is elsewhere (CPU, I/O, a lock).",
                    "Run the slowdown analysis to score CPU, GC, I/O and lock time against each other.",
                    null));
            }
        }
    }

    private static string Confidence(
        StackSnapshot? stacks, TraceAnalysis? trace, CounterSeries? queue)
    {
        if (queue is not null && (trace is not null || stacks is not null))
            return "high";
        if (queue is not null || trace is not null)
            return "medium";
        return "low";
    }

    private static IReadOnlyList<string> Measurements(
        CounterSeries? queue,
        CounterSeries? workers,
        int blockedThreads,
        int totalThreads,
        IoCategorySummary? sync,
        long? backlog,
        TraceAnalysis? trace)
    {
        var lines = new List<string>();
        if (queue is not null)
            lines.Add($"queue: peak {queue.Max:N0}, mean {queue.Mean:N0}, last {queue.Last:N0} over {queue.Points} sample(s)");
        if (workers is not null)
            lines.Add($"workers: min {workers.Min:N0}, mean {workers.Mean:N0}, max {workers.Max:N0}");
        if (backlog is not null)
            lines.Add($"trace backlog: {backlog:N0} (enqueued {trace?.ThreadPool.Enqueued ?? 0:N0}, " +
                      $"dequeued {trace?.ThreadPool.Dequeued ?? 0:N0}, max active workers {trace?.ThreadPool.MaxActiveWorkers ?? 0})");
        if (totalThreads > 0)
            lines.Add($"threads: {totalThreads}, {blockedThreads} blocked in a synchronous wait");
        if (sync is not null)
            lines.Add($"sync-over-async: {sync.BlockedMs:N0} ms across {sync.Sites} site(s) on {sync.Threads} thread(s)");
        return lines;
    }

    private static CounterSeries? Find(CounterCollectionResult? counters, string name) =>
        counters?.Series.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
