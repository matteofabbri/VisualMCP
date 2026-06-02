using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
    });

await builder.Build().RunAsync();
