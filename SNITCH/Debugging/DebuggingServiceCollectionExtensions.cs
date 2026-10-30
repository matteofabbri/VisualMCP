using Snitch.Debugging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the debugger: the netcoredbg locator/installer and the session service. The adapter is
/// an external process started per session, so nothing else in the container changes.
/// </summary>
public static class DebuggingServiceCollectionExtensions
{
    public static IServiceCollection AddSnitchDebugging(
        this IServiceCollection services, Action<DebugOptions>? configure = null)
    {
        var options = new DebugOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IDebuggerService, DebuggerService>();
        return services;
    }
}
