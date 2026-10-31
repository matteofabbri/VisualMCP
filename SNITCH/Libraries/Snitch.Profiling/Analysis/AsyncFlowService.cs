using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IAsyncFlowService
{
    /// <summary>The async story of a trace: tasks, await durations, incomplete state machines.</summary>
    AsyncFlowReport Analyze(string tracePath);
}

/// <summary>
/// Answers "what is the async code doing?" from <c>System.Threading.Tasks.TplEventSource</c>, the
/// provider the runtime writes its task and await events to. The numbers are exact event pairs, not
/// samples: <c>TaskWaitSend</c> is the moment a task starts awaiting another, <c>TaskWaitStop</c> the
/// moment it resumes, so the difference is the await itself — the time the async code was waiting,
/// which a CPU profile cannot show because the thread was doing something else.
///
/// The analysis is a pure function over the events, so a test can hand it a list; the service only
/// adds reading the file.
/// </summary>
public sealed class AsyncFlowService : IAsyncFlowService
{
    private const double LongAwaitMs = 500;

    private readonly ITraceEventReader _reader;

    public AsyncFlowService(ITraceEventReader reader) => _reader = reader;

    public AsyncFlowReport Analyze(string tracePath) => Analyze(_reader.Read(tracePath));

    public static AsyncFlowReport Analyze(TraceEventSet set)
    {
        var warnings = new List<string>();
        var events = set.Events
            .Where(record => record.Provider.Contains("TplEventSource", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (events.Count == 0)
        {
            var finding = new ProfilingFinding(
                "async-no-events", "warning",
                "The trace contains no task events",
                "System.Threading.Tasks.TplEventSource was not enabled for this trace, so there is no await or " +
                "task activity to analyse (a CPU-only trace has the sampling provider, not this one).",
                "Collect again with the async preset, which turns the provider on: " +
                "profile_collect profile=async, then run profile_async on the new trace.",
                "profile_collect profile=async (or providers='System.Threading.Tasks.TplEventSource:0x1FF:Informational').",
                null);

            return new AsyncFlowReport(
                set.Path, set.DurationMs, 0, 0, 0, 0, 0, 0, 0, [], [], [], 0,
                [finding],
                ["profile_collect profile=async, then profile_async on the new trace."],
                warnings);
        }

        long scheduled = 0, started = 0, completed = 0, exceptional = 0;
        var executionStarts = new Dictionary<int, TraceEventRecord>();
        var waitSends = new Dictionary<string, Queue<TraceEventRecord>>(StringComparer.Ordinal);
        var operationStarts = new Dictionary<string, Queue<TraceEventRecord>>(StringComparer.Ordinal);
        var waits = new List<AsyncWaitInfo>();
        var operations = new List<AsyncOperationInfo>();
        var incomplete = new List<IncompleteAsyncMethodInfo>();
        var unmatchedWaits = 0;
        var threads = new HashSet<int>();

        foreach (var record in events)
        {
            var name = EventText.Normalize(record.Name);
            threads.Add(record.ThreadId);

            if (name.StartsWith("taskscheduled", StringComparison.Ordinal))
            {
                scheduled++;
                continue;
            }

            // Start and stop are told apart by their payload, not by the name: a trace read through
            // TraceLog may present the pair as "TaskExecute/Start|Stop" or as one task name, and only
            // the stop event carries IsExceptional.
            if (name.StartsWith("taskexecute", StringComparison.Ordinal))
            {
                if (record.Payload.ContainsKey("IsExceptional"))
                {
                    completed++;
                    if (bool.TryParse(record.Value("IsExceptional"), out var isExceptional) && isExceptional)
                        exceptional++;
                    if (TaskId(record) is int stoppedTask) executionStarts.Remove(stoppedTask);
                }
                else
                {
                    started++;
                    if (TaskId(record) is int taskId) executionStarts[taskId] = record;
                }

                continue;
            }

            // TaskWaitSend carries Behavior; TaskWaitStop does not. That distinction survives whatever
            // the provider names the two events.
            if (name.StartsWith("taskwait", StringComparison.Ordinal))
            {
                if (record.Payload.ContainsKey("Behavior"))
                {
                    var key = WaitKey(record);
                    if (!waitSends.TryGetValue(key, out var queue))
                        waitSends[key] = queue = new Queue<TraceEventRecord>();
                    queue.Enqueue(record);
                    continue;
                }

                var stopKey = WaitKey(record);
                if (waitSends.TryGetValue(stopKey, out var pending) && pending.Count > 0)
                {
                    var start = pending.Dequeue();
                    var duration = record.TimeMs - start.TimeMs;
                    if (duration < 0) duration = 0;

                    waits.Add(new AsyncWaitInfo(
                        WaitingTaskId(start),
                        AwaitedTaskId(start),
                        start.ThreadId,
                        Math.Round(start.TimeMs, 1),
                        Math.Round(duration, 1),
                        start.Value("Behavior") ?? "Wait"));
                }
                else
                {
                    unmatchedWaits++;
                }

                continue;
            }

            if (name.StartsWith("incompleteasyncmethod", StringComparison.Ordinal))
            {
                incomplete.Add(new IncompleteAsyncMethodInfo(
                    record.Value("stateMachineDescription", "StateMachineDescription") ?? "(unnamed state machine)",
                    Math.Round(record.TimeMs, 1),
                    record.ThreadId));
                continue;
            }

            if (name.StartsWith("traceoperationstart", StringComparison.Ordinal))
            {
                var operationName = record.Value("OperationName", "operationName") ?? "(unnamed operation)";
                var key = $"{TaskId(record)}|{operationName}";
                if (!operationStarts.TryGetValue(key, out var queue))
                    operationStarts[key] = queue = new Queue<TraceEventRecord>();
                queue.Enqueue(record);
                continue;
            }

            if (name.StartsWith("traceoperationstop", StringComparison.Ordinal))
            {
                var operationName = record.Value("OperationName", "operationName") ?? "(unnamed operation)";
                var key = $"{TaskId(record)}|{operationName}";
                if (operationStarts.TryGetValue(key, out var queue) && queue.Count > 0)
                {
                    var start = queue.Dequeue();
                    operations.Add(new AsyncOperationInfo(
                        operationName,
                        TaskId(start) ?? 0,
                        Math.Round(record.TimeMs - start.TimeMs, 1)));
                }
            }
        }

        if (unmatchedWaits > 0)
            warnings.Add(
                $"{unmatchedWaits:N0} await(s) started but never resumed inside the trace window " +
                "(still awaiting when it ended, or the provider was disabled mid-run).");

        var totalAwaitMs = waits.Sum(wait => wait.DurationMs);
        var longest = waits
            .Where(wait => wait.DurationMs > 0)
            .OrderByDescending(wait => wait.DurationMs)
            .Take(10)
            .ToList();

        var topOperations = operations
            .GroupBy(operation => operation.Name, StringComparer.Ordinal)
            .Select(group => new AsyncOperationInfo(
                group.Key,
                group.First().TaskId,
                Math.Round(group.Sum(operation => operation.DurationMs), 1)))
            .OrderByDescending(operation => operation.DurationMs)
            .Take(15)
            .ToList();

        var findings = BuildFindings(
            set.DurationMs, scheduled, completed, exceptional, waits, totalAwaitMs, incomplete, longest);

        var nextSteps = findings
            .Select(finding => finding.SuggestedAction)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new AsyncFlowReport(
            set.Path,
            set.DurationMs,
            scheduled,
            started,
            completed,
            exceptional,
            waits.Count,
            Math.Round(totalAwaitMs, 1),
            longest.Count > 0 ? longest[0].DurationMs : 0,
            longest,
            topOperations,
            incomplete.Take(25).ToList(),
            threads.Count,
            findings,
            nextSteps,
            warnings);
    }

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        double durationMs,
        long scheduled,
        long completed,
        long exceptional,
        IReadOnlyList<AsyncWaitInfo> waits,
        double totalAwaitMs,
        IReadOnlyList<IncompleteAsyncMethodInfo> incomplete,
        IReadOnlyList<AsyncWaitInfo> longest)
    {
        var findings = new List<ProfilingFinding>();

        if (incomplete.Count > 0)
        {
            var names = incomplete
                .Select(method => method.Description)
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();

            findings.Add(new ProfilingFinding(
                "async-incomplete-method", incomplete.Count >= 5 ? "critical" : "warning",
                $"{incomplete.Count} async method(s) never completed",
                "The runtime reported these state machines as still alive when the trace ended" +
                (names.Count > 0 ? $": {string.Join(", ", names)}" : string.Empty) +
                ". A fire-and-forget task, a deadlock on a synchronisation context, or work that outlives the " +
                "request all look like this.",
                "Find who starts these methods and whether the returned Task is awaited. An unfinished async " +
                "method holds its state machine and everything it captured.",
                "profile_io on the same trace to see whether the thread is blocked inside the method.",
                null));
        }

        var longAwait = longest.FirstOrDefault(wait => wait.DurationMs >= LongAwaitMs);
        if (longAwait is not null)
        {
            findings.Add(new ProfilingFinding(
                "async-long-await", longAwait.DurationMs >= 3000 ? "critical" : "warning",
                $"One await took {longAwait.DurationMs:N0} ms",
                $"Task {longAwait.WaitingTaskId} awaited task {longAwait.AwaitedTaskId} for " +
                $"{longAwait.DurationMs:N0} ms on thread {longAwait.ThreadId} (behavior {longAwait.Behavior}). " +
                "That is I/O or a dependency being slow, not CPU work in this process.",
                "Look at what the awaited task talks to (database, HTTP, file system) and whether the request " +
                "can be batched or cached. profile_io / profile_database name the blocked call site.",
                "profile_io on the same trace, and profile_database when a database is involved.",
                null));
        }

        if (durationMs > 0 && totalAwaitMs / durationMs >= 0.4 && waits.Count >= 10)
        {
            findings.Add(new ProfilingFinding(
                "async-await-heavy", "info",
                "Awaiting dominates the trace window",
                $"{waits.Count:N0} awaits totalled {totalAwaitMs:N0} ms against a {durationMs:N0} ms window " +
                "(more than one await can be in flight, so the share can exceed the window).",
                "The non-CPU wait is the cost: profile_slowdown ranks it against CPU/GC/IO, and profile_io " +
                "names the site that blocks.",
                "profile_slowdown on the same trace.",
                null));
        }

        if (completed > 0 && exceptional * 100.0 / completed >= 5)
        {
            findings.Add(new ProfilingFinding(
                "async-exceptional-tasks", "warning",
                $"{exceptional:N0} task(s) completed with an exception",
                $"{exceptional:N0} of {completed:N0} completions ({exceptional * 100.0 / completed:0.0}%) were " +
                "exceptional. Exceptions thrown inside tasks are only observed when someone awaits them; " +
                "an unobserved one is both a correctness and a cost problem.",
                "Await the tasks, or attach a continuation that observes the exception. profile_analyze on an " +
                "exceptions trace names the throw sites.",
                "profile_analyze (exceptions) on the same trace.",
                null));
        }

        if (scheduled > 0 && scheduled - completed >= Math.Max(50, scheduled * 0.2))
        {
            findings.Add(new ProfilingFinding(
                "async-outstanding-tasks", "info",
                $"{scheduled - completed:N0} task(s) were scheduled but never completed",
                $"{scheduled:N0} scheduled, {completed:N0} completed in the window: work outlives the capture " +
                "or is queued behind a saturated scheduler.",
                "A large outstanding count with a quiet pool is normal at shutdown; with a busy pool use " +
                "profile_threadpool to check for starvation.",
                "profile_threadpool on the same trace.",
                null));
        }

        return findings;
    }

    private static int? TaskId(TraceEventRecord record)
    {
        var text = record.Value("TaskID", "taskId", "TaskId");
        return int.TryParse(text, out var value) ? value : null;
    }

    private static int WaitingTaskId(TraceEventRecord record)
    {
        var text = record.Value("OriginatingTaskID", "originatingTaskId", "originatingTaskID");
        return int.TryParse(text, out var value) ? value : -1;
    }

    private static int AwaitedTaskId(TraceEventRecord record) => TaskId(record) ?? -1;

    /// <summary>
    /// A wait is identified by the thread, the waiting task and the awaited task. The behavior is not part
    /// of the key: the runtime reports it on TaskWaitSend only, so including it would never match the stop.
    /// </summary>
    private static string WaitKey(TraceEventRecord record) =>
        $"{record.ThreadId}|{WaitingTaskId(record)}|{AwaitedTaskId(record)}";
}
