using Snitch.Profiling.Analysis;
using Snitch.Profiling.Model;
using Snitch.Profiling.Runtime;
using Snitch.Profiling.Snapshots;

namespace Snitch.Profiling.Verdicts;

public interface ISlowdownService
{
    /// <summary>
    /// Answers "why is this process slow?" against a live process: samples counters, collects a
    /// diagnose trace and (optionally) the stacks, then returns one verdict.
    /// </summary>
    Task<SlowdownReport> AnalyzeProcessAsync(
        int processId,
        string processName,
        int durationSeconds,
        bool includeStacks,
        string outputDirectory,
        int top = 15,
        bool resolveSource = true,
        string? userCodeRoot = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers "why is this trace slow?" from measurements that already exist. Counters and stacks
    /// are optional corroboration; with only a trace the answer is still produced but at lower
    /// confidence.
    /// </summary>
    Task<SlowdownReport> AnalyzeTraceAsync(
        string tracePath,
        CounterCollectionResult? counters = null,
        StackSnapshot? stacks = null,
        int top = 15,
        bool resolveSource = true,
        string? userCodeRoot = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The "why is it slow" verdict. It runs the observations the library already has — CPU samples,
/// GC, exceptions and contention from the trace analysis, blocked intervals from the I/O analysis,
/// counters and live stacks — and turns them into a ranked list of causes measured in the same
/// unit: how many seconds of the measured window each cause can explain.
///
/// That unit is the whole point. A trace can show 640 ms of CPU, 900 ms of I/O blocking and a
/// 2 000-item pool queue without saying which one made the request slow; scoring every candidate as
/// "share of wall time it accounts for" makes one of them win, and keeps the runners-up visible
/// with the reason they lost. When nothing reaches 10 % the answer is that the process was not
/// slow in the observed window — which is itself a useful verdict, not a failure.
/// </summary>
public sealed class SlowdownService : ISlowdownService
{
    private static readonly string[] IoCategories =
        ["File", "Http", "Network", "Database", "Pipe", "Console"];

    // Tie-break order when two causes explain exactly the same share of the window.
    private static readonly string[] CausePriority =
        ["cpu", "gc", "io-wait", "sync-over-async", "lock-wait", "blocked-native"];

    private readonly ICounterCollectionService _counters;
    private readonly ITraceCollectionService _traces;
    private readonly ITraceAnalysisService _analysis;
    private readonly IIoBlockService _io;
    private readonly IManagedStackService _stacks;
    private readonly IProfilingAdvisor _advisor;
    private readonly ProfilingOptions _options;

    public SlowdownService(
        ICounterCollectionService counters,
        ITraceCollectionService traces,
        ITraceAnalysisService analysis,
        IIoBlockService io,
        IManagedStackService stacks,
        IProfilingAdvisor advisor,
        ProfilingOptions options)
    {
        _counters = counters;
        _traces = traces;
        _analysis = analysis;
        _io = io;
        _stacks = stacks;
        _advisor = advisor;
        _options = options;
    }

    public async Task<SlowdownReport> AnalyzeProcessAsync(
        int processId,
        string processName,
        int durationSeconds,
        bool includeStacks,
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
                processId, counterFilter: null, durationSeconds: Math.Min(5, duration), intervalSeconds: 1,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"The counter session failed, so the trace carries the whole answer: {ex.Message}");
        }

        var safeName = string.Concat(processName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        Directory.CreateDirectory(outputDirectory);
        var tracePath = Path.Combine(
            outputDirectory, $"slowdown-{safeName}-{processId}-{DateTime.Now:yyyyMMdd-HHmmss}.nettrace");

        var collected = await _traces.CollectAsync(
            processId, tracePath, "diagnose", customProviders: null, duration,
            bufferMb: 256, requestStackwalk: true, cancellationToken).ConfigureAwait(false);

        StackSnapshot? stacks = null;
        if (includeStacks)
        {
            try
            {
                stacks = await _stacks.CaptureAsync(processId, maxFrames: 40, includeRuntimeFrames: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"The stack snapshot failed, so the answer is trace-only: {ex.Message}");
            }
        }

        var analysis = await _analysis.AnalyzeAsync(
            collected.Path, top, resolveSource, deep: true, cancellationToken).ConfigureAwait(false);
        var io = await _io.AnalyzeAsync(
            collected.Path, top, minPercent: 1.0, resolveSource,
            userCodeRoot ?? outputDirectory, cancellationToken).ConfigureAwait(false);

        return Build(analysis, io, counters, stacks, collected.Path, processName, processId, warnings);
    }

    public async Task<SlowdownReport> AnalyzeTraceAsync(
        string tracePath,
        CounterCollectionResult? counters = null,
        StackSnapshot? stacks = null,
        int top = 15,
        bool resolveSource = true,
        string? userCodeRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        var analysis = await _analysis.AnalyzeAsync(
            tracePath, top, resolveSource, deep: true, cancellationToken).ConfigureAwait(false);
        var io = await _io.AnalyzeAsync(
            tracePath, top, minPercent: 1.0, resolveSource, userCodeRoot, cancellationToken).ConfigureAwait(false);

        return Build(analysis, io, counters, stacks, tracePath, processName: null, processId: null, []);
    }

    private SlowdownReport Build(
        TraceAnalysis? analysis,
        IoBlockReport? io,
        CounterCollectionResult? counters,
        StackSnapshot? stacks,
        string? tracePath,
        string? processName,
        int? processId,
        List<string> warnings)
    {
        if (analysis is not null) warnings.AddRange(analysis.Warnings);
        if (io is not null) warnings.AddRange(io.Warnings);
        if (counters is not null) warnings.AddRange(counters.Warnings);

        var durationMs = FirstPositive(analysis?.Trace.DurationMs, io?.DurationMs, counters?.DurationMs);

        var causes = ScoreCauses(analysis, io, counters, stacks, durationMs);
        var weights = causes.Values.ToDictionary(c => c.Id, c => c.Weight, StringComparer.Ordinal);
        double Weight(string id) => weights.TryGetValue(id, out var value) ? value : 0;

        var ordered = causes.Values
            .OrderByDescending(c => c.Weight)
            .ThenBy(c => Array.IndexOf(CausePriority, c.Id))
            .ToList();
        var top = ordered.FirstOrDefault();

        string verdict;
        string answer;
        ProfilingFinding verdictFinding;

        if (top is null || top.Weight < AnswerBuilder.DominantThreshold)
        {
            verdict = "not-slow";
            (answer, verdictFinding) = NotSlow(analysis, io, counters, durationMs);
        }
        else
        {
            verdict = top.Id;
            (answer, verdictFinding) = Describe(top, analysis, io, counters, stacks, durationMs);
        }

        // The verdict is evidence #1 by construction: it is the sentence the caller reads first.
        var evidence = new List<AnswerEvidence>
        {
            new(1, Math.Round(top?.Weight ?? 0, 4), verdictFinding.Id, verdictFinding.Severity,
                verdictFinding.Title, verdictFinding.Evidence, verdictFinding.Recommendation,
                verdictFinding.SuggestedAction, verdictFinding.Source),
        };

        var supporting = new List<WeightedFinding>();
        if (analysis is not null)
            supporting.AddRange(_advisor.Analyze(analysis).Select(f =>
                new WeightedFinding(WeightFor(f.Id, Weight), f)));
        if (io is not null)
            supporting.AddRange(io.Findings.Select(f => new WeightedFinding(WeightFor(f.Id, Weight), f)));
        if (stacks is not null)
            supporting.AddRange(_advisor.Analyze(stacks).Select(f =>
                new WeightedFinding(WeightFor(f.Id, Weight) * 0.5, f)));
        if (counters is not null)
            supporting.AddRange(_advisor.Analyze(counters).Select(f =>
                new WeightedFinding(WeightFor(f.Id, Weight), f)));

        evidence.AddRange(AnswerBuilder.Rank(
            supporting.Where(s => s.Weight > 0 && s.Finding.Id != verdictFinding.Id), startRank: 2));

        var extraSteps = verdict switch
        {
            "not-slow" => new[]
            {
                "Re-run this analysis on a longer window collected while the slow request is in flight.",
            },
            "inconclusive" => new[]
            {
                "Pass tracePath, or call the live overload so counters, trace and stacks are collected together.",
            },
            _ => Array.Empty<string>(),
        };

        var nextSteps = AnswerBuilder.NextSteps(evidence, extraSteps);

        var confidence = Confidence(analysis, io, counters, stacks, durationMs);
        if (analysis is not null && analysis.Trace.DurationMs <= 0)
            warnings.Add("The trace reports no duration (it may be empty or truncated): the shares are not trustworthy.");
        if (analysis is null)
            warnings.Add("No trace analysis: CPU, GC and contention shares come from counters and stacks only.");

        return new SlowdownReport(
            new AnswerReport(
                "Why is the process slow?",
                verdict,
                answer,
                confidence,
                evidence,
                nextSteps,
                Measurements(analysis, io, counters, stacks, durationMs, top),
                warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            tracePath,
            processName,
            processId,
            ordered,
            analysis,
            io,
            stacks,
            counters);
    }

    private static Dictionary<string, CauseScore> ScoreCauses(
        TraceAnalysis? analysis,
        IoBlockReport? io,
        CounterCollectionResult? counters,
        StackSnapshot? stacks,
        double durationMs)
    {
        var causes = new Dictionary<string, CauseScore>(StringComparer.Ordinal);

        void Add(string id, string label, double weight, string detail)
        {
            if (weight <= 0)
                return;
            if (causes.TryGetValue(id, out var existing) && existing.Weight >= weight)
                return;
            causes[id] = new CauseScore(id, label, Math.Round(weight, 4), detail);
        }

        // ---- CPU: core-seconds burned per wall second ------------------------
        if (analysis is not null && durationMs > 0)
        {
            var hotspot = analysis.CpuHotspots.FirstOrDefault();
            Add("cpu", "CPU", analysis.Trace.TotalCpuMs / durationMs,
                $"{analysis.Trace.TotalCpuMs:N0} ms of CPU over {durationMs:N0} ms of wall time " +
                $"({analysis.Trace.TotalCpuMs / durationMs:0.00} core-seconds per second)" +
                (hotspot is null ? ", spread over several methods" : $"; top method {hotspot.Method} owns {hotspot.Share:0.0}%"));
        }
        else if (Find(counters, "cpu-usage") is { } cpu)
        {
            Add("cpu", "CPU", cpu.Max / 100.0,
                $"cpu-usage peaked at {cpu.Max:0.0}% of the machine (mean {cpu.Mean:0.0}%)");
        }

        // ---- GC: the pause share is already wall-clock time ------------------
        if (analysis is not null && analysis.Gc.PauseShare > 0)
        {
            var gc = analysis.Gc;
            Add("gc", "GC", gc.PauseShare / 100.0,
                $"{gc.TotalPauseMs:N0} ms paused in {gc.Gen0 + gc.Gen1 + gc.Gen2} collections " +
                $"({gc.PauseShare:0.0}% of the window), {AnswerBuilder.Mb(gc.SampledAllocationBytes)} sampled allocations");
        }
        else if (Find(counters, "time-in-gc") is { } gcCounter && gcCounter.Mean > 0)
        {
            Add("gc", "GC", gcCounter.Mean / 100.0,
                $"time-in-gc averaged {gcCounter.Mean:0.0}% of the sampled window");
        }

        // ---- I/O blocking: average number of threads parked in I/O ----------
        if (io is not null && io.DurationMs > 0)
        {
            double CategoryMs(params string[] names) =>
                io.Categories.Where(c => names.Contains(c.Category, StringComparer.Ordinal)).Sum(c => c.BlockedMs);

            var ioMs = CategoryMs(IoCategories);
            var ioTop = io.Categories.FirstOrDefault(c => IoCategories.Contains(c.Category, StringComparer.Ordinal));
            Add("io-wait", "I/O wait", ioMs / io.DurationMs,
                ioTop is null
                    ? $"{ioMs:N0} ms of I/O blocking"
                    : $"{ioMs:N0} ms of I/O blocking, most of it {ioTop.Category} ({ioTop.Share:0.0}% of blocked time)");

            var sync = CategoryMs("SyncOverAsync");
            var syncSites = io.Sites.Count(s => s.Category == "SyncOverAsync");
            Add("sync-over-async", "Sync over async", sync / io.DurationMs,
                $"{sync:N0} ms blocked in Task.Wait/Task.Result/GetAwaiter().GetResult() across {syncSites} site(s)");

            var nativeMs = CategoryMs("Sleep", "WaitHandle", "Other");
            var nativeTop = io.Categories.FirstOrDefault(c => c.Category is "Sleep" or "WaitHandle" or "Other");
            Add("blocked-native", "Explicit waits", nativeMs / io.DurationMs,
                $"{nativeMs:N0} ms parked in {(nativeTop is null ? "Sleep/WaitHandle" : nativeTop.Category)}");
        }

        // ---- Locks: take the larger of the two measurements, never their sum.
        // The I/O pass and the contention events describe the same waiting from two angles;
        // adding them would count one blocked thread twice.
        var ioLockMs = io is null
            ? 0
            : io.Categories.FirstOrDefault(c => c.Category == "Lock")?.BlockedMs ?? 0;
        var contentionMs = analysis?.Contention.TotalWaitMs ?? 0;
        var lockMs = Math.Max(ioLockMs, contentionMs);
        if (durationMs > 0 && lockMs > 0)
        {
            var monitor = stacks is null ? null : stacks.ContendedMonitors.FirstOrDefault();
            Add("lock-wait", "Lock wait", lockMs / durationMs,
                $"{lockMs:N0} ms waiting on locks" +
                (analysis is not null && contentionMs > 0
                    ? $" ({analysis.Contention.Events:N0} contention events, longest {analysis.Contention.MaxWaitMs:N0} ms)"
                    : "") +
                (monitor is null
                    ? ""
                    : $"; {monitor.WaitingThreads} thread(s) wait on a {AnswerBuilder.ShortName(monitor.ObjectType)} monitor held by thread {monitor.HolderThread}"));
        }

        // ---- Stacks-only fallback -------------------------------------------
        // Without a trace there is no time to divide, so the weight becomes the share of threads
        // that are currently parked. That is a different unit, and the detail says so; it only
        // runs when nothing time-based is available.
        if (analysis is null && io is null && counters is null && stacks is not null)
        {
            var threads = Math.Max(1, stacks.Threads.Count);
            var waiting = stacks.ContendedMonitors.Sum(m => m.WaitingThreads);
            Add("lock-wait", "Lock wait", Math.Min(1.0, waiting / (double)threads),
                $"{waiting} of {stacks.Threads.Count} threads wait on a monitor " +
                "(weight is the share of threads, not time: no trace was supplied)");

            var blocked = stacks.Threads.Count(t => t.Frames.Count > 0 && AnswerBuilder.IsBlockingFrame(t.Frames[0].Method));
            Add("blocked-native", "Explicit waits", Math.Min(1.0, blocked / (double)threads),
                $"{blocked} of {stacks.Threads.Count} threads are parked in a synchronous wait " +
                "(weight is the share of threads, not time: no trace was supplied)");
        }

        return causes;
    }

    private static (string Answer, ProfilingFinding Finding) Describe(
        CauseScore cause,
        TraceAnalysis? analysis,
        IoBlockReport? io,
        CounterCollectionResult? counters,
        StackSnapshot? stacks,
        double durationMs)
    {
        var severity = AnswerBuilder.SeverityFor(cause.Weight);
        var at = (string? source) => source is null ? "" : $" ({source})";

        switch (cause.Id)
        {
            case "cpu":
            {
                var hotspot = analysis is null ? null : analysis.CpuHotspots.FirstOrDefault();
                if (hotspot is not null)
                {
                    var answer =
                        $"CPU is the dominant cost: {hotspot.Method} owns {hotspot.Share:0.0}% of " +
                        $"{analysis!.Trace.TotalCpuMs:N0} ms of CPU ({cause.Weight:0.00} core-seconds per wall second, " +
                        $"≈{hotspot.EstimatedCpuMs:N0} ms in that method){at(hotspot.Source)}.";
                    return (answer, new ProfilingFinding(
                        "slowdown-cpu", severity,
                        $"CPU is the dominant cause, concentrated in {AnswerBuilder.ShortName(hotspot.Method)}",
                        $"{hotspot.Method}: {hotspot.Samples:N0} samples, {hotspot.Share:0.0}% of {analysis.Trace.CpuSamples:N0}, " +
                        $"≈{hotspot.EstimatedCpuMs:N0} ms of CPU in a {durationMs:N0} ms window.",
                        "Optimise the work in that method first: cache the result, reduce the iterations, or move it off the request path.",
                        hotspot.Source is null
                            ? "profile_calltree to find the caller that spends the time."
                            : $"Open {hotspot.Source}, change it, then re-run this analysis on a fresh trace.",
                        hotspot.Source));
                }

                var total = analysis?.Trace.TotalCpuMs ?? Find(counters, "cpu-usage")?.Max ?? 0;
                var answerSpread =
                    $"CPU is the dominant cost: the process used {total:N0} ms of CPU ({cause.Weight:0.00} of the window) " +
                    "and no single method owns it — it is spread across the top of the profile.";
                return (answerSpread, new ProfilingFinding(
                    "slowdown-cpu", severity,
                    "CPU is the dominant cause, spread over several methods",
                    cause.Detail,
                    "No single hotspot to fix: look at the shared caller of the top methods and at the allocation/GC cost of that work.",
                    "profile_calltree to see where the time is spent inclusively.",
                    null));
            }

            case "gc" when analysis is not null:
            {
                var gc = analysis.Gc;
                var answer =
                    $"Garbage collection is the dominant cost: {gc.PauseShare:0.0}% of the {durationMs:N0} ms window was spent " +
                    $"paused ({gc.TotalPauseMs:N0} ms in {gc.Gen0 + gc.Gen1 + gc.Gen2} collections, longest {gc.MaxPauseMs:N0} ms) " +
                    $"while {AnswerBuilder.Mb(gc.SampledAllocationBytes)} were allocated.";
                return (answer, new ProfilingFinding(
                    "slowdown-gc", severity,
                    "The GC pause is the dominant cause",
                    $"{gc.TotalPauseMs:N0} ms paused over {durationMs:N0} ms ({gc.PauseShare:0.0}%), " +
                    $"{gc.Gen2} gen-2 collections, {AnswerBuilder.Mb(gc.PromotedBytes)} promoted.",
                    "Reduce the allocation volume or lifetime; the allocation site is the actionable target, not the GC.",
                    "profile_allocations on a gc trace (profile='gc') to find the site that allocates the most.",
                    analysis.AllocationSites.FirstOrDefault()?.Source));
            }

            case "gc":
            {
                var gcCounter = Find(counters, "time-in-gc");
                var answer =
                    $"Garbage collection is the dominant cost: time-in-gc averages {gcCounter?.Mean ?? cause.Weight * 100:0.0}% " +
                    "of the sampled window (measured with counters only).";
                return (answer, new ProfilingFinding(
                    "slowdown-gc", severity,
                    "The GC is on the critical path",
                    cause.Detail,
                    "Reduce the allocation volume or lifetime; check Server GC for a multi-core service.",
                    "profile_collect with profile='gc' then profile_allocations.",
                    null));
            }

            case "io-wait":
            {
                var category = io!.Categories.First(c => IoCategories.Contains(c.Category, StringComparer.Ordinal));
                var site = io!.Sites.FirstOrDefault(s => IoCategories.Contains(s.Category, StringComparer.Ordinal));
                var answer =
                    $"The dominant cost is I/O blocking: {category.Category} waits account for {category.BlockedMs:N0} ms " +
                    $"({category.Share:0.0}% of blocked time, {category.Threads} thread(s)) with a {cause.Weight:0.00}-thread " +
                    $"average, concentrated in {AnswerBuilder.ShortName(site?.CallSite ?? category.Category)}{at(site?.Source)}.";
                return (answer, new ProfilingFinding(
                    "slowdown-io", severity,
                    $"I/O blocking is the dominant cause ({category.Category})",
                    site is null
                        ? $"{category.BlockedMs:N0} ms across {category.Sites} site(s) and {category.Threads} thread(s)."
                        : $"{site.CallSite} blocks in {site.BlockingMethod} for {site.BlockedMs:N0} ms " +
                          $"({site.Share:0.0}% of blocked time) on {site.Threads} thread(s).",
                    site?.Recommendation ?? "Switch the blocking call to its async API.",
                    site?.Source is null ? "profile_io to see every blocking site." : $"Open {site.Source}.",
                    site?.Source));
            }

            case "sync-over-async":
            {
                var sync = io!.Categories.First(c => c.Category == "SyncOverAsync");
                var site = io.Sites.FirstOrDefault(s => s.Category == "SyncOverAsync");
                var backlog = analysis?.ThreadPool.MaxBacklog ?? 0;
                var answer =
                    $"The process waits on its own work: {sync.BlockedMs:N0} ms ({sync.Share:0.0}% of blocked time, " +
                    $"{sync.Threads} thread(s)) are parked in Task.Wait/Task.Result/GetAwaiter().GetResult()" +
                    (site is null ? "" : $", at {AnswerBuilder.ShortName(site.CallSite)}{at(site.Source)}") +
                    (backlog >= 1000
                        ? $", while the thread-pool queue peaked at {backlog:N0} — that is thread-pool starvation."
                        : ".");
                return (answer, new ProfilingFinding(
                    "slowdown-sync-over-async", severity,
                    "Synchronous waits on asynchronous work are the dominant cause",
                    cause.Detail + (backlog > 0 ? $"; thread-pool queue peaked at {backlog:N0}." : "."),
                    "Replace the blocking wait with await. The work is already asynchronous; the parked thread is one the process cannot use.",
                    site?.Source is null
                        ? "profile_stacks to read the blocked stacks."
                        : $"Open {site.Source} and convert the wait to await.",
                    site?.Source));
            }

            case "lock-wait":
            {
                var contention = analysis?.Contention;
                var monitor = stacks is null ? null : stacks.ContendedMonitors.FirstOrDefault();
                var answer =
                    $"Lock contention is the dominant cost: {cause.Weight:0.00} of the window is spent waiting" +
                    (contention is null || contention.Events == 0
                        ? ""
                        : $" ({contention.Events:N0} contention events, {contention.TotalWaitMs:N0} ms in total, longest {contention.MaxWaitMs:N0} ms)") +
                    (monitor is null
                        ? "."
                        : $"; {monitor.WaitingThreads} thread(s) wait on a {AnswerBuilder.ShortName(monitor.ObjectType)} monitor held by thread {monitor.HolderThread}.");
                return (answer, new ProfilingFinding(
                    "slowdown-lock", severity,
                    "Lock contention is the dominant cause",
                    cause.Detail + (monitor?.HolderStack is null ? "" : $"; the holder's top frame is {monitor.HolderStack}."),
                    "Shrink the critical section, move I/O out of the lock, or partition it.",
                    monitor is null
                        ? "profile_stacks to name the holding thread."
                        : "Read the holder's stack: it is either slow in the critical section or blocked while holding the lock.",
                    monitor?.HolderStack));
            }

            case "blocked-native":
            {
                var site = io is null
                    ? null
                    : io.Sites.FirstOrDefault(s => s.Category is "Sleep" or "WaitHandle" or "Other");
                var answer =
                    $"Threads are parked in explicit waits: {cause.Weight:0.00} of the window (Sleep/WaitHandle)" +
                    (site is null ? "." : $", mostly {site.Category} at {AnswerBuilder.ShortName(site.CallSite)}{at(site.Source)}.");
                return (answer, new ProfilingFinding(
                    "slowdown-blocked-native", severity,
                    "Explicit thread waits are the dominant cause",
                    cause.Detail,
                    site?.Recommendation ?? "Replace Thread.Sleep/WaitHandle with Task.Delay/await or a TaskCompletionSource.",
                    site?.Source is null ? "profile_io to see every blocking site." : $"Open {site.Source}.",
                    site?.Source));
            }

            default:
                return ($"The dominant cost is {cause.Label}.", new ProfilingFinding(
                    $"slowdown-{cause.Id}", severity, $"{cause.Label} is the dominant cause",
                    cause.Detail, "Follow the evidence below.", null, null));
        }
    }

    private static (string Answer, ProfilingFinding Finding) NotSlow(
        TraceAnalysis? analysis, IoBlockReport? io, CounterCollectionResult? counters, double durationMs)
    {
        var cpuMs = analysis?.Trace.TotalCpuMs ?? 0;
        var cpuRatio = durationMs > 0 ? cpuMs / durationMs : 0;
        var gcShare = analysis?.Gc.PauseShare ?? Find(counters, "time-in-gc")?.Mean ?? 0;
        var blockedMs = io?.TotalBlockedMs ?? 0;
        var queue = Find(counters, "threadpool-queue-length")?.Max
            ?? (double)(analysis?.ThreadPool.MaxBacklog ?? 0);

        var window = durationMs > 0 ? $"{durationMs:N0} ms" : "the sampled window";
        var answer = durationMs > 0
            ? $"No dominant cost: over {window} the process used {cpuMs:N0} ms of CPU ({cpuRatio:0.00} core-seconds per wall second), " +
              $"{gcShare:0.0}% in GC and {blockedMs:N0} ms blocked" +
              (queue > 0 ? $", with a pool queue peak of {queue:N0}" : "") +
              " — it was mostly idle, or the slow work happened outside the measured window."
            : $"No dominant cost in the sampled window: the strongest signal is below {AnswerBuilder.DominantThreshold:P0} of the window.";

        return (answer, new ProfilingFinding(
            "slowdown-none", "info",
            "No cause explains a meaningful share of the window",
            $"cpu {cpuMs:N0} ms over {window}, gc pause {gcShare:0.0}%, blocked {blockedMs:N0} ms, pool queue peak {queue:N0}; " +
            $"the highest-scoring cause is below {AnswerBuilder.DominantThreshold:P0} of wall time.",
            "Collect a longer window while the slow request is in flight, or start the trace before triggering the work.",
            "profile_collect action='start', trigger the slow request, action='stop', then re-run this analysis.",
            null));
    }

    /// <summary>
    /// Maps a finding id to the share of the window its cause explains, so the evidence list is
    /// ordered by the same unit as the verdict. Ids that describe a cost rather than a cause (JIT,
    /// exceptions, allocations) stay visible but below every real cause.
    /// </summary>
    private static double WeightFor(string id, Func<string, double> weight) => id switch
    {
        "cpu-hotspot" or "cpu-concentration" or "cpu-spike" or "cpu-saturated" or "cpu-bound" => weight("cpu"),
        "gc-pause" or "gc-time-high" or "gen2-pressure" or "gen2-rate" or "loh-pressure" or "gc-bound" => weight("gc"),
        "io-block" or "io-category" or "io-pressure" => weight("io-wait"),
        "sync-over-async" or "thread-pool-blocking" or "threadpool-starvation" or "threadpool-queue" or "pool-starvation"
            => weight("sync-over-async"),
        "lock-contention" or "monitor-contention" or "lock-contention-count" => weight("lock-wait"),
        "thread-blocking" => Math.Max(weight("lock-wait"), weight("blocked-native")),
        "blocked-threads" => Math.Max(weight("lock-wait"), weight("sync-over-async")) * 0.5,
        "exception-storm" or "exception-rate" or "exception-site" or "allocation-hotspot" or "allocation-site" or
            "allocation-rate" or "allocation-rate-high" or "finalizer-pressure" or "jit-time" or "allocation-spike"
            => 0.05,
        _ => 0,
    };

    private static string Confidence(
        TraceAnalysis? analysis, IoBlockReport? io, CounterCollectionResult? counters, StackSnapshot? stacks, double durationMs)
    {
        if (analysis is not null && analysis.Trace.DurationMs <= 0)
            return "low";
        if (analysis is not null && (counters is not null || stacks is not null))
            return "high";
        if (analysis is not null || io is not null)
            return "medium";
        if (counters is not null && counters.Series.Count > 0 && durationMs > 0)
            return "low";
        return "low";
    }

    private static IReadOnlyList<string> Measurements(
        TraceAnalysis? analysis, IoBlockReport? io, CounterCollectionResult? counters, StackSnapshot? stacks,
        double durationMs, CauseScore? top)
    {
        var lines = new List<string>();
        if (durationMs > 0)
            lines.Add($"window: {durationMs:N0} ms");
        if (analysis is not null)
        {
            var hotspot = analysis.CpuHotspots.FirstOrDefault();
            lines.Add($"cpu: {analysis.Trace.TotalCpuMs:N0} ms over {analysis.Trace.DurationMs:N0} ms " +
                      $"({(analysis.Trace.DurationMs > 0 ? analysis.Trace.TotalCpuMs / analysis.Trace.DurationMs : 0):0.00} core-seconds per second)");
            lines.Add(hotspot is null
                ? "hotspot: none (the profile is spread or the process was idle)"
                : $"hotspot: {hotspot.Method} {hotspot.Share:0.0}% ({(hotspot.Source ?? "no source")})");
            var gc = analysis.Gc;
            lines.Add($"gc: {gc.Gen0 + gc.Gen1 + gc.Gen2} collections, {gc.PauseShare:0.0}% paused, longest {gc.MaxPauseMs:N0} ms");
            lines.Add($"contention: {analysis.Contention.Events:N0} events, {analysis.Contention.TotalWaitMs:N0} ms, longest {analysis.Contention.MaxWaitMs:N0} ms");
            lines.Add($"thread pool: max backlog {analysis.ThreadPool.MaxBacklog:N0}, max workers {analysis.ThreadPool.MaxActiveWorkers}");
        }
        if (io is not null)
        {
            lines.Add($"blocked: {io.TotalBlockedMs:N0} ms over {io.Sites.Count} site(s), " +
                      $"{io.AverageBlockedThreads:0.00} blocked threads on average, {io.IdleMs:N0} ms idle-parked (excluded)");
        }
        if (counters is not null)
        {
            foreach (var name in new[] { "cpu-usage", "time-in-gc", "threadpool-queue-length", "threadpool-thread-count" })
            {
                var series = Find(counters, name);
                if (series is not null)
                    lines.Add($"{name}: mean {series.Mean:0.##}, max {series.Max:0.##} over {series.Points} sample(s)");
            }
        }
        if (stacks is not null)
        {
            var blocked = stacks.Threads.Count(t => t.Frames.Count > 0 && AnswerBuilder.IsBlockingFrame(t.Frames[0].Method));
            lines.Add($"stacks: {stacks.Threads.Count} thread(s), {blocked} blocked, {stacks.ContendedMonitors.Count} contended monitor(s)");
        }
        if (top is not null)
            lines.Add($"strongest cause: {top.Id} = {top.Weight:0.00} of the window");
        return lines;
    }

    private static CounterSeries? Find(CounterCollectionResult? counters, string name) =>
        counters?.Series.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static double FirstPositive(params double?[] values)
    {
        foreach (var value in values)
            if (value is > 0)
                return value.Value;
        return 0;
    }
}
