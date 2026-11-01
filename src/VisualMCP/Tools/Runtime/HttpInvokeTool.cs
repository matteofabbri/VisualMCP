using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Runtime;

namespace VisualMCP.Tools.Runtime;

[McpServerToolType]
public static class HttpInvokeTool
{
    [McpServerTool, Description(
        "Sends an HTTP request to any URL (local or remote) and returns the status code, response body, " +
        "content-type and elapsed time. Use this to call REST APIs of running applications — no browser or " +
        "terminal needed. Supports all HTTP methods, custom request headers, and JSON/text bodies. " +
        "Self-signed certificates on localhost are accepted automatically.")]
    public static Task<HttpInvokeResult> HttpInvoke(
        IHttpInvokeService svc,
        [Description("HTTP method: GET, POST, PUT, PATCH, DELETE, HEAD or OPTIONS.")] string method,
        [Description("Full URL, e.g. https://localhost:5001/api/products/42.")] string url,
        [Description("Optional request body. JSON strings are sent as 'application/json'; anything else as 'text/plain'.")] string? body = null,
        [Description("Optional JSON object of extra request headers, e.g. {\"Authorization\":\"Bearer eyJ…\",\"X-Tenant\":\"acme\"}.")] string? headersJson = null,
        [Description("Request timeout in seconds (default 30).")] int timeoutSeconds = 30)
        => svc.HttpInvoke(method, url, body, headersJson, timeoutSeconds);
}