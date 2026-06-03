using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Runtime;

[McpServerToolType]
public static class SignalRTool
{
    private static SignalRSessionService Sessions => SignalRSessionService.Instance;

    // ── Connect / disconnect ─────────────────────────────────────────────────

    [McpServerTool, Description(
        "Connects to a SignalR hub and returns a sessionId used by all other signalr_* tools. " +
        "Supports any ASP.NET Core SignalR hub — no special server-side package required. " +
        "Pass auth headers (e.g. Authorization: Bearer …) if the hub requires authentication.")]
    public static async Task<object> SignalRConnect(
        [Description("Full URL of the SignalR hub endpoint, e.g. https://localhost:5001/chatHub.")] string hubUrl,
        [Description("Optional JSON object of HTTP headers added during negotiation, e.g. {\"Authorization\":\"Bearer eyJ…\"}.")] string? headersJson = null,
        [Description("Connection timeout in seconds (default 10).")] int timeoutSeconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));
        try
        {
            var builder = new HubConnectionBuilder()
                .WithUrl(hubUrl, opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };

                    if (!string.IsNullOrWhiteSpace(headersJson))
                    {
                        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                        if (headers is not null)
                            foreach (var (k, v) in headers)
                                opts.Headers[k] = v;
                    }
                })
                .WithAutomaticReconnect();

            var conn = builder.Build();
            await conn.StartAsync(cts.Token);

            var sessionId = Sessions.AddSession(conn);
            return new
            {
                sessionId,
                connectionId = conn.ConnectionId,
                state        = conn.State.ToString(),
                hub          = hubUrl,
            };
        }
        catch (OperationCanceledException)
        {
            return new { error = $"Connection timed out after {timeoutSeconds}s." };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [McpServerTool, Description(
        "Closes a SignalR session and frees all its resources. " +
        "Call this when you are done with the hub connection.")]
    public static async Task<object> SignalRDisconnect(
        [Description("Session ID returned by signalr_connect.")] string sessionId)
    {
        var removed = await Sessions.RemoveAsync(sessionId);
        return removed
            ? (object)new { disconnected = true,  sessionId }
            : (object)new { error = $"Session '{sessionId}' not found. Active sessions: [{string.Join(", ", Sessions.ListIds())}]" };
    }

    // ── Subscribe / events ───────────────────────────────────────────────────

    [McpServerTool, Description(
        "Registers a listener for a hub method so that incoming server messages are buffered " +
        "and readable via signalr_events. The first argument of the method is captured as JSON; " +
        "call this once per method you want to observe before invoking any hub action.")]
    public static Task<object> SignalRSubscribe(
        [Description("Session ID returned by signalr_connect.")] string sessionId,
        [Description("Hub method name to listen for, e.g. 'ReceiveMessage' or 'OnOrderUpdated'.")] string methodName,
        [Description("Number of parameters the server sends (1–4, default 1). Each parameter is captured as a JSON value.")] int paramCount = 1)
    {
        var session = Sessions.Get(sessionId);
        if (session is null)
            return Task.FromResult<object>(new { error = $"Session '{sessionId}' not found." });

        IDisposable sub = paramCount switch
        {
            0 => session.Connection.On(methodName, () =>
                    session.Events.Enqueue(new { method = methodName, args = Array.Empty<object>(), ts = DateTimeOffset.UtcNow })),
            2 => session.Connection.On<JsonElement, JsonElement>(methodName, (a1, a2) =>
                    session.Events.Enqueue(new { method = methodName, args = new[] { a1, a2 }, ts = DateTimeOffset.UtcNow })),
            3 => session.Connection.On<JsonElement, JsonElement, JsonElement>(methodName, (a1, a2, a3) =>
                    session.Events.Enqueue(new { method = methodName, args = new[] { a1, a2, a3 }, ts = DateTimeOffset.UtcNow })),
            4 => session.Connection.On<JsonElement, JsonElement, JsonElement, JsonElement>(methodName, (a1, a2, a3, a4) =>
                    session.Events.Enqueue(new { method = methodName, args = new[] { a1, a2, a3, a4 }, ts = DateTimeOffset.UtcNow })),
            _ => session.Connection.On<JsonElement>(methodName, arg =>  // default: 1 param
                    session.Events.Enqueue(new { method = methodName, args = new[] { arg }, ts = DateTimeOffset.UtcNow })),
        };

        session.AddSubscription(sub);
        return Task.FromResult<object>(new { subscribed = methodName, paramCount, sessionId });
    }

    [McpServerTool, Description(
        "Returns events buffered by signalr_subscribe since the last call. " +
        "Optionally waits up to waitMs milliseconds for at least one event to arrive.")]
    public static async Task<object> SignalREvents(
        [Description("Session ID returned by signalr_connect.")] string sessionId,
        [Description("Milliseconds to wait for the first event before returning (default 500, max 10 000). Pass 0 to return immediately.")] int waitMs = 500,
        [Description("Maximum number of events to dequeue and return (default 50).")] int maxEvents = 50)
    {
        var session = Sessions.Get(sessionId);
        if (session is null)
            return new { error = $"Session '{sessionId}' not found." };

        waitMs = Math.Clamp(waitMs, 0, 10_000);

        if (waitMs > 0 && session.Events.IsEmpty)
            await Task.Delay(waitMs);

        var events = new List<object>(maxEvents);
        while (events.Count < maxEvents && session.Events.TryDequeue(out var e))
            events.Add(e);

        return new { sessionId, eventCount = events.Count, events };
    }

    // ── Invoke ───────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Calls a method on a connected SignalR hub and returns the result (if the method returns a value). " +
        "Use signalr_subscribe + signalr_events to capture server-pushed messages instead.")]
    public static async Task<object> SignalRInvoke(
        [Description("Session ID returned by signalr_connect.")] string sessionId,
        [Description("Hub method name to call, e.g. 'SendMessage' or 'JoinGroup'.")] string methodName,
        [Description("Optional JSON array of arguments, e.g. [\"roomA\", \"hello world\"]. Omit for no-argument methods.")] string? argsJson = null,
        [Description("Whether the hub method returns a value (default true). Set to false for fire-and-forget (SendAsync).")] bool hasReturnValue = true)
    {
        var session = Sessions.Get(sessionId);
        if (session is null)
            return new { error = $"Session '{sessionId}' not found." };

        object?[] args = [];
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                var elements = JsonSerializer.Deserialize<JsonElement[]>(argsJson);
                args = elements?.Cast<object?>().ToArray() ?? [];
            }
            catch (JsonException ex)
            {
                return new { error = $"Invalid argsJson: {ex.Message}" };
            }
        }

        try
        {
            if (hasReturnValue)
            {
                var result = await session.Connection.InvokeCoreAsync<object>(methodName, args);
                return new { method = methodName, result, sessionId };
            }
            else
            {
                await session.Connection.SendCoreAsync(methodName, args);
                return new { method = methodName, sent = true, sessionId };
            }
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Returns connection lifecycle log entries (connected, reconnecting, closed) " +
        "for a SignalR session — useful for debugging connection problems.")]
    public static Task<object> SignalRLogs(
        [Description("Session ID returned by signalr_connect.")] string sessionId)
    {
        var session = Sessions.Get(sessionId);
        if (session is null)
            return Task.FromResult<object>(new { error = $"Session '{sessionId}' not found." });

        var logs = new List<string>();
        while (session.Logs.TryDequeue(out var l)) logs.Add(l);
        return Task.FromResult<object>(new { sessionId, logs });
    }

    [McpServerTool, Description(
        "Lists all active SignalR sessions (IDs, hub URLs, connection states). " +
        "Useful to check what is currently open before connecting again.")]
    public static Task<object> SignalRListSessions()
    {
        var ids = Sessions.ListIds();
        var sessions = ids.Select(id =>
        {
            var s = Sessions.Get(id);
            return s is null ? null : new { sessionId = id, state = s.Connection.State.ToString() };
        }).Where(x => x is not null).ToList();

        return Task.FromResult<object>(new { count = sessions.Count, sessions });
    }
}
