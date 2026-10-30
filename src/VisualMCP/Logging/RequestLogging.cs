using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace VisualMCP.Logging;

/// <summary>
/// Logs every JSON-RPC message the client sends to the server — requests,
/// notifications and client responses — with its method, payload and duration.
/// </summary>
/// <remarks>
/// <see cref="ToolCallLogging"/> only sees tool invocations; this filter sits
/// below it and captures the whole incoming envelope (initialize, tools/list,
/// notifications, sampling responses, …), which is what lands in the log file.
/// </remarks>
public static class RequestLoggingExtensions
{
    const int MaxPayloadChars = 2000;

    /// <summary>
    /// Intercepts all incoming JSON-RPC messages and writes them to the
    /// "VisualMCP.Requests" logger. Call after the server is configured.
    /// </summary>
    public static IMcpServerBuilder WithRequestLogging(this IMcpServerBuilder builder)
    {
        builder.WithMessageFilters(filters =>
        {
            filters.AddIncomingFilter(next =>
                (context, cancellationToken) => LogAsync(next, context, cancellationToken));
        });
        return builder;
    }

    static async Task LogAsync(
        McpMessageHandler next,
        MessageContext context,
        CancellationToken cancellationToken)
    {
        var logger = (context.Services ?? context.Server.Services)
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger("VisualMCP.Requests");

        if (logger is null)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (context.JsonRpcMessage)
        {
            case JsonRpcRequest request:
            {
                var sw = Stopwatch.StartNew();
                logger.LogInformation(">>> REQUEST {Method} id={Id} {Params}",
                    request.Method, request.Id.ToString(), FormatPayload(request.Params));
                try
                {
                    await next(context, cancellationToken).ConfigureAwait(false);
                    sw.Stop();
                    logger.LogInformation("<<< REQUEST {Method} id={Id} ({Ms} ms)",
                        request.Method, request.Id.ToString(), sw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    sw.Stop();
                    logger.LogInformation("<<< REQUEST CANCELED {Method} id={Id} ({Ms} ms)",
                        request.Method, request.Id.ToString(), sw.ElapsedMilliseconds);
                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    logger.LogError(ex, "<<< REQUEST FAILED {Method} id={Id} ({Ms} ms)",
                        request.Method, request.Id.ToString(), sw.ElapsedMilliseconds);
                    throw;
                }
                break;
            }

            case JsonRpcNotification notification:
                logger.LogInformation(">>> NOTIFICATION {Method} {Params}",
                    notification.Method, FormatPayload(notification.Params));
                await next(context, cancellationToken).ConfigureAwait(false);
                break;

            case JsonRpcResponse response:
                logger.LogDebug(">>> RESPONSE id={Id}", response.Id.ToString());
                await next(context, cancellationToken).ConfigureAwait(false);
                break;

            case JsonRpcError error:
                logger.LogDebug(">>> ERROR RESPONSE id={Id} {Error}",
                    error.Id.ToString(), error.Error?.Message);
                await next(context, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await next(context, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    static string FormatPayload(JsonNode? node)
    {
        if (node is null) return "(no params)";
        try
        {
            var json = node.ToJsonString();
            return json.Length > MaxPayloadChars
                ? json[..MaxPayloadChars] + $"…(truncated, {json.Length} chars)"
                : json;
        }
        catch
        {
            return "(unserializable params)";
        }
    }
}
