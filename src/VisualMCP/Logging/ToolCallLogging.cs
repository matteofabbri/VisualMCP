using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace VisualMCP.Logging;

/// <summary>
/// Wraps a tool so every invocation is logged with its name, arguments,
/// duration and outcome. The MCP SDK does not log the tool name itself, so we
/// capture it here at the one place every call funnels through.
/// </summary>
sealed class LoggingDelegatingTool(McpServerTool inner, ILogger logger) : DelegatingMcpServerTool(inner)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Params?.Name ?? "(unknown)";
        var args = FormatArguments(request.Params?.Arguments);
        var sw = Stopwatch.StartNew();

        logger.LogInformation(">>> TOOL CALL {Tool} {Args}", name, args);
        try
        {
            var result = await base.InvokeAsync(request, cancellationToken);
            sw.Stop();
            logger.LogInformation("<<< TOOL DONE {Tool} ({Ms} ms){Err}",
                name, sw.ElapsedMilliseconds, result?.IsError == true ? " [reported error]" : "");
            return result!;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "<<< TOOL FAILED {Tool} ({Ms} ms)", name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    static string FormatArguments(IReadOnlyDictionary<string, JsonElement>? args)
    {
        if (args is null || args.Count == 0) return "(no args)";
        try
        {
            var json = JsonSerializer.Serialize(args);
            return json.Length > 1000 ? json[..1000] + "…(truncated)" : json;
        }
        catch
        {
            return "(unserializable args)";
        }
    }
}

public static class ToolCallLoggingExtensions
{
    /// <summary>
    /// Decorates every registered <see cref="McpServerTool"/> with
    /// <see cref="LoggingDelegatingTool"/> so all tool calls are logged.
    /// Call after the tools have been registered (e.g. WithToolsFromAssembly).
    /// </summary>
    public static IMcpServerBuilder WithToolCallLogging(this IMcpServerBuilder builder)
    {
        var services = builder.Services;
        for (int i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.ServiceType != typeof(McpServerTool)) continue;

            if (d.IsKeyedService)
            {
                var key = d.ServiceKey;
                services[i] = ServiceDescriptor.DescribeKeyed(
                    typeof(McpServerTool), key,
                    (sp, k) => Wrap(MaterializeKeyed(d, sp, k), sp),
                    d.Lifetime);
            }
            else
            {
                services[i] = ServiceDescriptor.Describe(
                    typeof(McpServerTool),
                    sp => Wrap(Materialize(d, sp), sp),
                    d.Lifetime);
            }
        }
        return builder;
    }

    static McpServerTool Wrap(McpServerTool inner, IServiceProvider sp)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("VisualMCP.ToolCalls");
        return new LoggingDelegatingTool(inner, logger);
    }

    static McpServerTool Materialize(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is McpServerTool inst) return inst;
        if (d.ImplementationFactory is not null) return (McpServerTool)d.ImplementationFactory(sp);
        if (d.ImplementationType is not null) return (McpServerTool)ActivatorUtilities.CreateInstance(sp, d.ImplementationType);
        throw new InvalidOperationException("Cannot materialize McpServerTool service descriptor.");
    }

    static McpServerTool MaterializeKeyed(ServiceDescriptor d, IServiceProvider sp, object? key)
    {
        if (d.KeyedImplementationInstance is McpServerTool inst) return inst;
        if (d.KeyedImplementationFactory is not null) return (McpServerTool)d.KeyedImplementationFactory(sp, key);
        if (d.KeyedImplementationType is not null) return (McpServerTool)ActivatorUtilities.CreateInstance(sp, d.KeyedImplementationType);
        throw new InvalidOperationException("Cannot materialize keyed McpServerTool service descriptor.");
    }
}
