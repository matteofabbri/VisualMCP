using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;

namespace VisualMCP.Tools.Runtime;

/// <summary>Represents one live SignalR hub connection together with its event/log queues.</summary>
public sealed class SignalRSession : IAsyncDisposable
{
    public HubConnection Connection { get; }
    public ConcurrentQueue<object> Events { get; } = new();
    public ConcurrentQueue<string> Logs    { get; } = new();

    private readonly List<IDisposable> _subscriptions = [];

    public SignalRSession(HubConnection connection)
    {
        Connection = connection;
        connection.Closed      += ex => { Logs.Enqueue($"[closed]       {ex?.Message ?? "clean shutdown"}"); return Task.CompletedTask; };
        connection.Reconnecting += ex => { Logs.Enqueue($"[reconnecting] {ex?.Message}");                    return Task.CompletedTask; };
        connection.Reconnected  += id => { Logs.Enqueue($"[reconnected]  connectionId={id}");                return Task.CompletedTask; };
    }

    public void AddSubscription(IDisposable sub) => _subscriptions.Add(sub);

    public async ValueTask DisposeAsync()
    {
        foreach (var sub in _subscriptions)
            try { sub.Dispose(); } catch { /* best-effort */ }

        try { await Connection.StopAsync(); }    catch { /* best-effort */ }
        await Connection.DisposeAsync();
    }
}

/// <summary>
/// Process-lifetime singleton that manages all active SignalR sessions.
/// Follows the same Instance pattern as <c>RoslynWorkspaceService</c>.
/// </summary>
public sealed class SignalRSessionService
{
    public static readonly SignalRSessionService Instance = new();
    private SignalRSessionService() { }

    private readonly ConcurrentDictionary<string, SignalRSession> _sessions = new();

    /// <summary>Registers a new, already-started connection and returns its session ID.</summary>
    public string AddSession(HubConnection connection)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _sessions[id] = new SignalRSession(connection);
        return id;
    }

    public SignalRSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public async Task<bool> RemoveAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return false;

        await session.DisposeAsync();
        return true;
    }

    public IReadOnlyList<string> ListIds() => [.. _sessions.Keys];
}
