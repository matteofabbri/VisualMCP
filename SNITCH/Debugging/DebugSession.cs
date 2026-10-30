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

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Per source file, the breakpoints that were last sent to the adapter.</summary>
    public ConcurrentDictionary<string, BreakpointSpec[]> Breakpoints { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sequence of the last event this session consumed as a stop/exit.</summary>
    public long LastStopSequence { get; set; }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
