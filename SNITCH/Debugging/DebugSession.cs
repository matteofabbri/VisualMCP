using System.Collections.Concurrent;

namespace Snitch.Debugging;

/// <summary>
/// One live debug session: the adapter connection, what the session is doing right now, and the
/// breakpoints that are set. The state is written by the service on every stop/continue, so a tool
/// call that arrives after the fact still describes where the process is.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    public DebugSession(string id, DapClient client, string mode)
    {
        Id = id;
        Client = client;
        Mode = mode;
    }

    public string Id { get; }
    public DapClient Client { get; }

    /// <summary>"launch" or "attach".</summary>
    public string Mode { get; }

    /// <summary>The program under debug, when known.</summary>
    public string? Program { get; init; }

    /// <summary>
    /// What a restart repeats: the launch arguments as they were given. Null for an attached
    /// session, which has no launcher to repeat.
    /// </summary>
    public LaunchSpec? Launch { get; set; }

    /// <summary>The process id of the debuggee, when the adapter reports one.</summary>
    public int? DebuggeeProcessId { get; set; }

    /// <summary>"starting", "running", "stopped" or "exited".</summary>
    public string State { get; set; } = "starting";

    /// <summary>The thread that is stopped, when the last stop event named one.</summary>
    public int? StoppedThreadId { get; set; }

    /// <summary>The adapter's reason for the last stop ("breakpoint", "step", "exception", "pause", …).</summary>
    public string? StopReason { get; set; }

    /// <summary>The line the process is stopped on, when known.</summary>
    public string? StopLocation { get; set; }

    /// <summary>The file of the stopped frame, used to find the breakpoint that produced the stop.</summary>
    public string? StopFile { get; set; }

    /// <summary>The line of the stopped frame, 1-based.</summary>
    public int StopLine { get; set; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Per source file, the breakpoints that were last sent to the adapter.</summary>
    public ConcurrentDictionary<string, BreakpointSpec[]> Breakpoints { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per <c>file:line</c>, how many times a hit-conditioned breakpoint has been reached.</summary>
    public ConcurrentDictionary<string, int> HitCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many stops the hit-condition filter absorbed.</summary>
    public int HitConditionSkips { get; set; }

    /// <summary>The function breakpoints that were last sent to the adapter.</summary>
    public FunctionBreakpointSpec[] FunctionBreakpoints { get; set; } = [];

    /// <summary>The data breakpoints of this session, keyed by member name.</summary>
    public ConcurrentDictionary<string, DataBreakpoint> DataBreakpoints { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exception filters the adapter accepted ("all", "user-unhandled").</summary>
    public string[] ExceptionFilters { get; set; } = [];

    /// <summary>The exception type conditions that accompany the first filter (<c>!Type</c> negates).</summary>
    public string[] ExceptionTypes { get; set; } = [];

    /// <summary>The sequence of the last event this session consumed as a stop/exit.</summary>
    public long LastStopSequence { get; set; }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
