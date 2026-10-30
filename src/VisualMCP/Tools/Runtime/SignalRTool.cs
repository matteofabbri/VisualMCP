using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Runtime;

namespace VisualMCP.Tools.Runtime;

[McpServerToolType]
public static class SignalRTool
{
    [McpServerTool, Description(
        "Connects to a SignalR hub and returns a sessionId used by all other signal_r_* tools. " +
        "Supports any ASP.NET Core SignalR hub — no special server-side package required. " +
        "Pass auth headers (e.g. Authorization: Bearer …) if the hub requires authentication.")]
    public static Task<object> SignalRConnect(
        ISignalRService svc,
        [Description("Full URL of the SignalR hub endpoint, e.g. https://localhost:5001/chatHub.")] string hubUrl,
        [Description("Optional JSON object of HTTP headers added during negotiation, e.g. {\"Authorization\":\"Bearer eyJ…\"}.")] string? headersJson = null,
        [Description("Connection timeout in seconds (default 10).")] int timeoutSeconds = 10)
        => svc.SignalRConnect(hubUrl, headersJson, timeoutSeconds);

    [McpServerTool, Description(
        "Closes a SignalR session and frees all its resources. " +
        "Call this when you are done with the hub connection.")]
    public static Task<object> SignalRDisconnect(
        ISignalRService svc,
        [Description("Session ID returned by signal_r_connect.")] string sessionId)
        => svc.SignalRDisconnect(sessionId);

    [McpServerTool, Description(
        "Registers a listener for a hub method so that incoming server messages are buffered " +
        "and readable via signal_r_events. The first argument of the method is captured as JSON; " +
        "call this once per method you want to observe before invoking any hub action.")]
    public static Task<object> SignalRSubscribe(
        ISignalRService svc,
        [Description("Session ID returned by signal_r_connect.")] string sessionId,
        [Description("Hub method name to listen for, e.g. 'ReceiveMessage' or 'OnOrderUpdated'.")] string methodName,
        [Description("Number of parameters the server sends (1-4, default 1). Each parameter is captured as a JSON value.")] int paramCount = 1)
        => svc.SignalRSubscribe(sessionId, methodName, paramCount);

    [McpServerTool, Description(
        "Returns events buffered by signal_r_subscribe since the last call. " +
        "Optionally waits up to waitMs milliseconds for at least one event to arrive.")]
    public static Task<object> SignalREvents(
        ISignalRService svc,
        [Description("Session ID returned by signal_r_connect.")] string sessionId,
        [Description("Milliseconds to wait for the first event before returning (default 500, max 10 000). Pass 0 to return immediately.")] int waitMs = 500,
        [Description("Maximum number of events to dequeue and return (default 50).")] int maxEvents = 50)
        => svc.SignalREvents(sessionId, waitMs, maxEvents);

    [McpServerTool, Description(
        "Calls a method on a connected SignalR hub and returns the result (if the method returns a value). " +
        "Use signal_r_subscribe + signal_r_events to capture server-pushed messages instead.")]
    public static Task<object> SignalRInvoke(
        ISignalRService svc,
        [Description("Session ID returned by signal_r_connect.")] string sessionId,
        [Description("Hub method name to call, e.g. 'SendMessage' or 'JoinGroup'.")] string methodName,
        [Description("Optional JSON array of arguments, e.g. [\"roomA\", \"hello world\"]. Omit for no-argument methods.")] string? argsJson = null,
        [Description("Whether the hub method returns a value (default true). Set to false for fire-and-forget (SendAsync).")] bool hasReturnValue = true)
        => svc.SignalRInvoke(sessionId, methodName, argsJson, hasReturnValue);

    [McpServerTool, Description(
        "Returns connection lifecycle log entries (connected, reconnecting, closed) " +
        "for a SignalR session — useful for debugging connection problems.")]
    public static Task<object> SignalRLogs(
        ISignalRService svc,
        [Description("Session ID returned by signal_r_connect.")] string sessionId)
        => svc.SignalRLogs(sessionId);

    [McpServerTool, Description(
        "Lists all active SignalR sessions (IDs, hub URLs, connection states). " +
        "Useful to check what is currently open before connecting again.")]
    public static Task<object> SignalRListSessions(ISignalRService svc)
        => svc.SignalRListSessions();
}