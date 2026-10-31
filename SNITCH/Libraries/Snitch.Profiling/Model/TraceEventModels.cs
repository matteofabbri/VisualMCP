namespace Snitch.Profiling.Model;

// ---------------------------------------------------------------------------
// Raw EventPipe events (the Events Viewer and the provider-specific analyzers)
// ---------------------------------------------------------------------------

/// <summary>
/// One event as it came out of the trace, with its payload already formatted to strings. The raw shape
/// is deliberate: every provider is read the same way, so a provider no parser knows still shows its
/// event name and fields instead of being dropped.
/// </summary>
public sealed record TraceEventRecord(
    string Provider,
    string Name,
    double TimeMs,
    int ThreadId,
    Guid ActivityId,
    Guid RelatedActivityId,
    string Level,
    IReadOnlyDictionary<string, string?> Payload)
{
    /// <summary>The first non-empty value among the given payload names (case-insensitive).</summary>
    public string? Value(params string[] names)
    {
        foreach (var name in names)
        {
            if (Payload.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>The first payload value that parses as a number, among the given names.</summary>
    public double? Number(params string[] names)
    {
        var text = Value(names);
        return double.TryParse(
            text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

/// <summary>One provider/event row of the inventory, with how often it fired and a sample payload.</summary>
public sealed record TraceEventTypeRow(
    string Provider,
    string Name,
    long Count,
    double FirstMs,
    double LastMs,
    double PerSecond,
    string Level,
    IReadOnlyList<string> PayloadFields,
    IReadOnlyDictionary<string, string?> Sample);

/// <summary>
/// The answer to "what events does this trace contain?": every provider/event pair with its count and
/// a sample payload, so a trace collected with custom providers can be read without a provider-specific
/// tool — the Events Viewer, for EventPipe.
/// </summary>
public sealed record EventInventoryReport(
    string TracePath,
    double DurationMs,
    long Events,
    int EventsLost,
    bool Truncated,
    int Providers,
    int EventTypes,
    IReadOnlyList<TraceEventTypeRow> Rows,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// .NET async (System.Threading.Tasks.TplEventSource)
// ---------------------------------------------------------------------------

/// <summary>One await: the originating task waited on another task for <see cref="DurationMs"/>.</summary>
public sealed record AsyncWaitInfo(
    int WaitingTaskId,
    int AwaitedTaskId,
    int ThreadId,
    double StartMs,
    double DurationMs,
    string Behavior);

/// <summary>One async operation the runtime named (AsyncCausalityOperation) with its total duration.</summary>
public sealed record AsyncOperationInfo(
    string Name,
    int TaskId,
    double DurationMs);

/// <summary>An async method whose state machine was still alive when the trace ended.</summary>
public sealed record IncompleteAsyncMethodInfo(string Description, double TimeMs, int ThreadId);

/// <summary>
/// The answer to "what is the async code doing?": scheduled/started/completed tasks, the exact await
/// durations (TplEventSource pairs TaskWaitSend with TaskWaitStop), the tasks that threw, the named
/// async operations and the state machines that never completed.
/// </summary>
public sealed record AsyncFlowReport(
    string TracePath,
    double DurationMs,
    long ScheduledTasks,
    long StartedTasks,
    long CompletedTasks,
    long ExceptionalTasks,
    long AwaitCount,
    double TotalAwaitMs,
    double LongestAwaitMs,
    IReadOnlyList<AsyncWaitInfo> LongestAwaits,
    IReadOnlyList<AsyncOperationInfo> Operations,
    IReadOnlyList<IncompleteAsyncMethodInfo> IncompleteAsyncMethods,
    int Threads,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Database (EF Core / SqlClient EventSource events)
// ---------------------------------------------------------------------------

/// <summary>One command the trace reported, with the text and the time it took when the provider told us.</summary>
public sealed record DatabaseCommandInfo(
    string Provider,
    string EventName,
    string Text,
    double TimeMs,
    double DurationMs,
    int ThreadId,
    string? ConnectionId,
    string? CommandId);

/// <summary>Commands that share a normalised shape (literals replaced), with their cost.</summary>
public sealed record DatabaseCommandGroup(
    string Shape,
    string Sample,
    int Count,
    double TotalMs,
    double MeanMs,
    double MaxMs);

/// <summary>
/// The answer to "what is the database doing?": the commands found in the trace, the shapes that cost
/// the most, the slowest single commands, connection activity, and the N+1/round-trip signals those
/// numbers imply. <see cref="Captured"/> is false when the trace holds no database event at all — the
/// report then names the providers it did see and how to collect it again.
/// </summary>
public sealed record DatabaseTraceReport(
    string TracePath,
    double DurationMs,
    bool Captured,
    IReadOnlyList<string> ProvidersSeen,
    int DatabaseEvents,
    int CommandCount,
    double TotalCommandMs,
    int ConnectionsOpened,
    IReadOnlyList<DatabaseCommandInfo> Slowest,
    IReadOnlyList<DatabaseCommandGroup> TopShapes,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);

// ---------------------------------------------------------------------------
// Instrumentation (exact call counts and times)
// ---------------------------------------------------------------------------

/// <summary>One instrumented method, as the injected probe reported it.</summary>
public sealed record InstrumentedMethodStat(
    int MethodId,
    string Method,
    long Calls,
    double InclusiveMs,
    double ExclusiveMs,
    double MaxInclusiveMs,
    double Share,
    int MaxDepth);

/// <summary>What instrumenting an assembly did.</summary>
public sealed record InstrumentationResult(
    string AssemblyPath,
    string OutputPath,
    string MapPath,
    string ProbeProvider,
    int Types,
    int Methods,
    int Skipped,
    long Bytes,
    IReadOnlyList<string> Notes);

/// <summary>
/// The answer to "how many times did each method run, and how long did it take?": exact enter/leave
/// pairs recorded by the probe the instrumenter injected into the assembly — no sampling, so a method
/// that runs twice is reported with two calls, and the inclusive/exclusive split is exact.
/// </summary>
public sealed record InstrumentationReport(
    string TracePath,
    double DurationMs,
    bool Captured,
    string? MapPath,
    long Calls,
    int Methods,
    int Threads,
    long Unfinished,
    IReadOnlyList<InstrumentedMethodStat> MethodsByCost,
    IReadOnlyList<InstrumentedMethodStat> MethodsByCalls,
    IReadOnlyList<ProfilingFinding> Findings,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);
