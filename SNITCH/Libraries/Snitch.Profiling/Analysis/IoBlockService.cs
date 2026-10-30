using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IIoBlockService
{
    Task<IoBlockReport> AnalyzeAsync(
        string tracePath, int top, double minPercent, bool resolveSource,
        string? userCodeRoot, CancellationToken cancellationToken = default);
}

/// <summary>
/// Finds where threads are blocked in I/O. In an EventPipe trace with stack walks, a thread sampled
/// outside managed code (ClrThreadSampleType.External) is waiting in native code; the managed stack
/// attached to that sample is the call that blocked. Consecutive samples bound the wait, so the
/// result is "how long, where and in which kind of I/O" rather than a single snapshot:
///
/// File, Http, Network (sockets), Database, Pipe, Console, SyncOverAsync (Task.Wait/Result — the
/// thread is waiting on work that is already asynchronous), Lock, Sleep, WaitHandle and Other.
///
/// Each site carries the user frame to open and the blocking framework frame, so the fix is either
/// "make this call async" or "this lock is the real problem".
/// </summary>
public sealed class IoBlockService : IIoBlockService
{
    private readonly ITraceStackReader _reader;

    public IoBlockService(ITraceStackReader reader) => _reader = reader;

    public Task<IoBlockReport> AnalyzeAsync(
        string tracePath, int top, double minPercent, bool resolveSource,
        string? userCodeRoot, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        top = Math.Clamp(top <= 0 ? 15 : top, 1, 100);
        minPercent = Math.Clamp(minPercent <= 0 ? 1 : minPercent, 0.1, 50);

        return Task.Run(() => Analyze(tracePath, top, minPercent, resolveSource, userCodeRoot), cancellationToken);
    }

    private IoBlockReport Analyze(
        string tracePath, int top, double minPercent, bool resolveSource, string? userCodeRoot)
    {
        var model = _reader.Read(tracePath, resolveSource);
        var warnings = new List<string>(model.Warnings);
        var threshold = minPercent / 100.0;

        var sites = new Dictionary<(string Category, string CallSite, string? Source), SiteAggregate>();
        var categories = new Dictionary<string, CategoryAggregate>(StringComparer.Ordinal);
        var idleMs = 0.0;
        var total = 0.0;

        foreach (var sample in model.BlockedSamples)
        {
            var callSite = UserCode.DeepestUserFrame(sample.Frames, userCodeRoot);
            var (category, blockingMethod) = Classify(sample.Frames);
            if (category == "Idle" && callSite is not null)
                category = "Other";
            if (category == "WaitHandle" && callSite is null)
                category = "Idle";
            if (category == "Idle")
            {
                // A thread-pool worker parked on its completion port is idle, not application I/O.
                idleMs += sample.DurationMs;
                continue;
            }

            total += sample.DurationMs;
            callSite ??= sample.Frames.FirstOrDefault(f => f.Method == blockingMethod)
                         ?? sample.Frames.FirstOrDefault();

            var callSiteMethod = callSite?.Method ?? "(unknown)";
            var source = callSite?.Source;
            var key = (category, callSiteMethod, source);
            if (!sites.TryGetValue(key, out var site))
            {
                site = new SiteAggregate
                {
                    Category = category,
                    CallSite = callSiteMethod,
                    Source = source,
                    BlockingMethod = blockingMethod,
                    Stack = sample.Frames.Take(12).Select(f => f.Method).ToList(),
                };
            }

            site.BlockedMs += sample.DurationMs;
            site.Samples++;
            site.Threads.Add(sample.ThreadId);
            sites[key] = site;

            if (!categories.TryGetValue(category, out var categoryAggregate))
            {
                categoryAggregate = new CategoryAggregate { Category = category };
                categories[category] = categoryAggregate;
            }
            categoryAggregate.BlockedMs += sample.DurationMs;
            categoryAggregate.Threads.Add(sample.ThreadId);
            categoryAggregate.Sites.Add(callSiteMethod);
        }

        var totalForShare = Math.Max(1.0, total);
        var siteEntries = sites.Values
            .Select(s => new IoBlockSite(
                s.Category,
                s.CallSite,
                s.Source,
                s.BlockingMethod,
                Math.Round(s.BlockedMs, 1),
                Math.Round(s.BlockedMs * 100.0 / totalForShare, 2),
                s.Threads.Count,
                s.Samples,
                s.Stack,
                Recommend(s.Category, s.BlockingMethod)))
            .Where(s => s.Share >= minPercent)
            .OrderByDescending(s => s.BlockedMs)
            .Take(top)
            .ToList();

        var categoryEntries = categories.Values
            .Select(c => new IoCategorySummary(
                c.Category,
                Math.Round(c.BlockedMs, 1),
                Math.Round(c.BlockedMs * 100.0 / totalForShare, 2),
                c.Sites.Count,
                c.Threads.Count))
            .OrderByDescending(c => c.BlockedMs)
            .ToList();

        var findings = BuildFindings(siteEntries, categoryEntries, total, model.DurationMs, warnings);
        if (total <= 0)
            warnings.Add(
                "No blocking intervals were found. The process may not have been waiting, or the trace was collected " +
                "without stack walks (profile='runtime' or 'diagnose' requests them).");
        if (idleMs > 0)
            warnings.Add(
                $"{idleMs / 1000.0:0.0} s of thread-pool/idle waiting were ignored (parked pool workers are not application blocking).");

        return new IoBlockReport(
            Path.GetFullPath(tracePath),
            Math.Round(model.DurationMs, 1),
            Math.Round(total, 1),
            Math.Round(idleMs, 1),
            model.DurationMs <= 0 ? 0 : Math.Round(total / model.DurationMs, 2),
            siteEntries,
            categoryEntries,
            findings,
            warnings);
    }

    private static (string Category, string BlockingMethod) Classify(IReadOnlyList<TraceFrameInfo> frames)
    {
        // A synchronous wait is the mechanism even when the leaf is WaitHandle.WaitOne.
        var sync = frames.FirstOrDefault(f => IsSyncOverAsync(f.Method));
        if (sync is not null)
            return ("SyncOverAsync", sync.Method);

        var idle = frames.FirstOrDefault(f => IsIdleWait(f.Method));
        if (idle is not null)
            return ("Idle", idle.Method);

        foreach (var frame in frames)
        {
            var method = frame.Method;

            if (method.Contains("Monitor.Wait", StringComparison.Ordinal) ||
                method.Contains("Monitor.Enter", StringComparison.Ordinal) ||
                method.Contains("SemaphoreSlim.Wait", StringComparison.Ordinal) ||
                method.Contains("Mutex.WaitOne", StringComparison.Ordinal) ||
                method.Contains("ReaderWriterLockSlim.Enter", StringComparison.Ordinal) ||
                method.Contains("SpinLock.Enter", StringComparison.Ordinal))
                return ("Lock", method);

            if (method.Contains("Thread.Sleep", StringComparison.Ordinal))
                return ("Sleep", method);

            if (method.Contains("System.IO.Pipes", StringComparison.Ordinal) ||
                method.Contains("NamedPipe", StringComparison.Ordinal) ||
                method.Contains("PipeStream", StringComparison.Ordinal))
                return ("Pipe", method);

            if (method.Contains("System.Net.Http", StringComparison.Ordinal) ||
                method.Contains("HttpClient", StringComparison.Ordinal) ||
                method.Contains("SocketsHttpHandler", StringComparison.Ordinal))
                return ("Http", method);

            if (method.Contains("System.Net.Sockets", StringComparison.Ordinal) ||
                method.Contains("Socket.", StringComparison.Ordinal) ||
                method.Contains("NetworkStream", StringComparison.Ordinal) ||
                method.Contains("TcpClient", StringComparison.Ordinal) ||
                method.Contains("UdpClient", StringComparison.Ordinal))
                return ("Network", method);

            if (method.Contains("System.Data.", StringComparison.Ordinal) ||
                method.Contains("SqlCommand", StringComparison.Ordinal) ||
                method.Contains("SqlConnection", StringComparison.Ordinal) ||
                method.Contains("Npgsql", StringComparison.Ordinal) ||
                method.Contains("MySql", StringComparison.Ordinal) ||
                method.Contains("DbCommand", StringComparison.Ordinal))
                return ("Database", method);

            if (method.Contains("System.IO.", StringComparison.Ordinal) ||
                method.Contains("FileStream", StringComparison.Ordinal) ||
                method.Contains("StreamReader", StringComparison.Ordinal) ||
                method.Contains("StreamWriter", StringComparison.Ordinal) ||
                method.Contains("File.Read", StringComparison.Ordinal) ||
                method.Contains("File.Write", StringComparison.Ordinal))
                return ("File", method);

            if (method.Contains("System.Console", StringComparison.Ordinal) ||
                method.Contains("Console.Read", StringComparison.Ordinal))
                return ("Console", method);

            if (method.Contains("WaitHandle.Wait", StringComparison.Ordinal))
                return ("WaitHandle", method);
        }

        return ("Other", frames.Count > 0 ? frames[0].Method : "(unknown)");
    }

    private static bool IsSyncOverAsync(string method) =>
        method.Contains("Task.Wait", StringComparison.Ordinal) ||
        (method.Contains("Task", StringComparison.Ordinal) && method.Contains("get_Result", StringComparison.Ordinal)) ||
        method.Contains("Awaiter.GetResult", StringComparison.Ordinal);

    private static bool IsIdleWait(string method) =>
        method.Contains("GetQueuedCompletionStatus", StringComparison.Ordinal) ||
        method.Contains("LowLevelLifoSemaphore.WaitForSignal", StringComparison.Ordinal) ||
        method.Contains("NtWaitForSingleObject", StringComparison.Ordinal) ||
        method.Contains("NtWaitForMultipleObjects", StringComparison.Ordinal) ||
        method.Contains("WaitForMultipleObjects", StringComparison.Ordinal);

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        IReadOnlyList<IoBlockSite> sites, IReadOnlyList<IoCategorySummary> categories,
        double totalBlockedMs, double durationMs, List<string> warnings)
    {
        var findings = new List<ProfilingFinding>();
        var top = sites.FirstOrDefault();
        var ioTop = sites.FirstOrDefault(s => IsIoCategory(s.Category));

        if (ioTop is not null && ioTop.Share >= 25 && ioTop.BlockedMs >= 500)
            findings.Add(new ProfilingFinding(
                "io-block", ioTop.Share >= 50 ? "critical" : "warning",
                $"I/O blocking in {ShortName(ioTop.CallSite)}",
                $"{ioTop.Category}: {ioTop.BlockedMs:N0} ms ({ioTop.Share:0.0}% of blocked time) across {ioTop.Threads} thread(s), " +
                $"blocking in {ioTop.BlockingMethod}" + (ioTop.Source is null ? "." : $" (source: {ioTop.Source})."),
                ioTop.Recommendation,
                ioTop.Source is null ? "profile_stacks to see the blocked threads right now." : $"Open {ioTop.Source}",
                ioTop.Source));

        var waitTop = sites.FirstOrDefault(s =>
            s.Category is "Sleep" or "Lock" or "WaitHandle" or "Other" &&
            s.Share >= 25 && s.BlockedMs >= 500);
        if (waitTop is not null)
            findings.Add(new ProfilingFinding(
                "thread-blocking", waitTop.Share >= 50 ? "warning" : "info",
                $"Threads are waiting in {waitTop.Category}",
                $"{waitTop.BlockedMs:N0} ms ({waitTop.Share:0.0}% of blocked time) in {waitTop.CallSite}" +
                (waitTop.Source is null ? "." : $" (source: {waitTop.Source})."),
                waitTop.Recommendation,
                waitTop.Category == "Lock"
                    ? "profile_stacks to see the holding thread."
                    : "Open the call site and move the wait off the request path.",
                waitTop.Source));

        var sync = categories.FirstOrDefault(c => c.Category == "SyncOverAsync");
        if (sync is not null && sync.BlockedMs >= 500 && sync.Share >= 10)
            findings.Add(new ProfilingFinding(
                "sync-over-async", sync.Share >= 30 ? "critical" : "warning",
                "Synchronous wait on asynchronous work",
                $"{sync.BlockedMs:N0} ms ({sync.Share:0.0}% of blocked time) in Task.Wait/Task.Result/GetAwaiter().GetResult " +
                $"across {sync.Sites} site(s) and {sync.Threads} thread(s).",
                "Replace the wait with await. The work is already asynchronous; the thread is parked for the whole duration, " +
                "and on the thread pool that also starves other requests.",
                "profile_stacks to see the blocked threads and their callers.",
                null));

        var pool = sites.FirstOrDefault(s =>
            s.Category == "SyncOverAsync" &&
            s.Stack.Any(m => m.Contains("PortableThreadPool", StringComparison.Ordinal) ||
                             m.Contains("ThreadPoolWorkQueue", StringComparison.Ordinal) ||
                             m.Contains("Task.ExecuteWithThreadLocal", StringComparison.Ordinal)));
        if (pool is not null)
            findings.Add(new ProfilingFinding(
                "thread-pool-blocking", "warning",
                "A thread-pool thread is blocked on a synchronous wait",
                $"{pool.CallSite} blocks a pool thread for {pool.BlockedMs:N0} ms ({pool.Share:0.0}% of blocked time).",
                "This is the thread-pool starvation pattern: the pool cannot run other work while this thread waits. Make the call async.",
                "Convert the call to await, then profile_diagnose to confirm the queue stays empty.",
                pool.Source));

        var slowCategory = categories.FirstOrDefault(c =>
            IsIoCategory(c.Category) && c.Category != "SyncOverAsync" &&
            c.BlockedMs >= 500 && c.Share >= 20);
        if (slowCategory is not null)
            findings.Add(new ProfilingFinding(
                "io-category", "info",
                $"Most blocking is {slowCategory.Category} I/O",
                $"{slowCategory.BlockedMs:N0} ms ({slowCategory.Share:0.0}% of blocked time) across {slowCategory.Sites} site(s).",
                CategoryRecommendation(slowCategory.Category),
                "Open the top site's source and switch that call to its async API.",
                top?.Source));

        if (totalBlockedMs >= Math.Max(1000, durationMs * 0.5) && top is not null)
            findings.Add(new ProfilingFinding(
                "io-pressure", "info",
                "Threads spend a large part of the trace waiting",
                $"{totalBlockedMs:N0} ms of blocked time over {durationMs:N0} ms of trace " +
                $"({(durationMs <= 0 ? 0 : totalBlockedMs / durationMs):0.0} blocked threads on average).",
                "Check whether the waits are on the critical path: an I/O call that every request makes is a design issue, not a tuning one.",
                null,
                top.Source));

        if (totalBlockedMs <= 0)
            findings.Add(new ProfilingFinding(
                "no-io-blocking", "info",
                "No I/O blocking was observed",
                "The trace contains no external (blocked) intervals with a managed stack.",
                "If the process is expected to block, collect a longer window under load and make sure requestStackwalk was enabled.",
                "profile_collect profile='runtime' or 'diagnose'.",
                null));

        return findings.OrderByDescending(f => SeverityRank(f.Severity)).ToList();
    }

    private static bool IsIoCategory(string category) =>
        category is "File" or "Http" or "Network" or "Database" or "Pipe" or "Console" or "SyncOverAsync";

    private static string Recommend(string category, string blockingMethod) => category switch
    {
        "SyncOverAsync" => $"Blocking in {blockingMethod}: replace Wait/Result/GetResult with await. The thread is parked while the work is already asynchronous, and on the thread pool this starves other requests.",
        "File" => "Synchronous file I/O: use ReadAsync/WriteAsync (or batch reads) instead of blocking the thread on the file system.",
        "Http" => "Synchronous HTTP: use HttpClient.SendAsync and reuse one HttpClient; a synchronous Send blocks a thread for the whole round trip.",
        "Network" => "Synchronous socket I/O: use the async Socket/NetworkStream APIs.",
        "Database" => "Synchronous database call: use the async ADO.NET methods (ExecuteReaderAsync, …).",
        "Pipe" => "Synchronous pipe I/O: use the async pipe APIs.",
        "Console" => "Console I/O is synchronous; move it off the request path or put it behind a queue.",
        "Lock" => "This is lock contention rather than I/O; profile_stacks names the holding thread.",
        "Sleep" => "Thread.Sleep parks a thread; use Task.Delay/await.",
        "WaitHandle" => "A WaitHandle blocks the thread; prefer an async wait (TaskCompletionSource, SemaphoreSlim.WaitAsync).",
        _ => "Blocked in native code below this call; inspect the stack and move the work off the thread if it is I/O.",
    };

    private static string CategoryRecommendation(string category) => category switch
    {
        "File" => "Switch the file access to async or batch small reads.",
        "Http" => "Use SendAsync with a shared HttpClient.",
        "Network" => "Use the async socket APIs.",
        "Database" => "Use the async ADO.NET APIs.",
        "Pipe" => "Use the async pipe APIs.",
        "Console" => "Move console output off the hot path.",
        _ => "Switch the blocking call to its async API.",
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1,
    };

    private static string ShortName(string fullName) => FrameNames.ShortName(fullName);

    private sealed class SiteAggregate
    {
        public string Category { get; init; } = "";
        public string CallSite { get; init; } = "";
        public string? Source { get; init; }
        public string BlockingMethod { get; init; } = "";
        public IReadOnlyList<string> Stack { get; init; } = [];
        public double BlockedMs { get; set; }
        public long Samples { get; set; }
        public HashSet<int> Threads { get; } = [];
    }

    private sealed class CategoryAggregate
    {
        public string Category { get; init; } = "";
        public double BlockedMs { get; set; }
        public HashSet<int> Threads { get; } = [];
        public HashSet<string> Sites { get; } = new(StringComparer.Ordinal);
    }
}
