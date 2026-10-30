using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snitch.Storage;

namespace VisualMCP.Logging;

/// <summary>
/// Wraps a tool so every invocation is logged with its name, arguments,
/// duration and outcome. The MCP SDK does not log the tool name itself, so we
/// capture it here at the one place every call funnels through — including the
/// row it appends to the solution's command log in SQLite.
/// </summary>
sealed class LoggingDelegatingTool(McpServerTool inner, ILogger logger, IVisualMcpStore store)
    : DelegatingMcpServerTool(inner)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Params?.Name ?? "(unknown)";
        var args = CommandLogFormatter.Format(name, request.Params?.Arguments);
        var sw = Stopwatch.StartNew();

        // Recorded as the call arrives, before it runs: a call that fails or is cancelled still
        // happened, and that is exactly what the audit needs.
        store.RecordCommand(name, args);

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
        var store = sp.GetRequiredService<IVisualMcpStore>();
        return new LoggingDelegatingTool(inner, logger, store);
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
