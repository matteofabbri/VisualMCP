using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using VisualMCP.Logging;

// Redirect stdout to null immediately: the MCP stdio transport uses the raw
// stdout stream directly, so Console.SetOut does not affect it, but it does
// prevent any stray text (host startup banners, log lines) from corrupting
// the JSON protocol before our FileLoggerProvider takes over.
Console.SetOut(TextWriter.Null);

// Must be called before any MSBuild/Roslyn types are JIT-compiled.
MSBuildLocator.RegisterDefaults();

await RunServerAsync(args);

// Separate method so the JIT doesn't compile Roslyn types before RegisterDefaults().
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static async Task RunServerAsync(string[] args)
{
    var logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "mcp-servers", "VisualMCP", "debug.log");

    using var host = Host.CreateDefaultBuilder(args)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new FileLoggerProvider(logPath));
            logging.SetMinimumLevel(LogLevel.Debug);
        })
        .ConfigureServices(services =>
        {
            services.AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();
        })
        .Build();

    await host.RunAsync();
}
