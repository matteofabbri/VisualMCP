using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using VisualMCP.Logging;
using VisualMCP.Workspace;

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

    const string ServerInstructions =
        "VisualMCP exposes Roslyn-powered, semantically-accurate analysis, navigation and " +
        "refactoring for C#/.NET solutions.\n\n" +
        "WHEN TO USE: Whenever you are working on a .NET solution (.sln/.slnx), prefer these " +
        "tools over plain text search (grep) or reading files by hand. They use the compiler's " +
        "semantic model, so they correctly resolve overloads, overrides, interface members, " +
        "generics and partial types that text matching gets wrong.\n\n" +
        "ROUTING:\n" +
        "- Find where a symbol is used / who calls a method -> find_references, find_callers " +
        "(NOT grep — grep also hits comments, strings and unrelated same-named text).\n" +
        "- Locate a class/method/interface by name -> find_symbol.\n" +
        "- Find implementations of an interface or derived types -> find_implementations, find_derived_types.\n" +
        "- Inspect a type's members or a symbol's signature/docs -> get_type_members, get_symbol_info.\n" +
        "- Dead code, code smells, complexity, metrics, security, DI, duplicate/async issues -> the Analysis tools " +
        "(reliable detection needs the control-flow graph, which only Roslyn can build — do NOT infer these by reading source).\n" +
        "- Rename, extract, change signature, move type, etc. -> the Refactoring tools (semantic and safe, " +
        "they update every reference; do NOT hand-edit for these).\n" +
        "- Compiler errors/warnings for the whole solution -> get_diagnostics.\n\n" +
        "SETUP: The solution in the working directory is auto-discovered and loaded on demand, so you can " +
        "call any tool directly. Only call load_solution if a tool reports that no solution could be located, " +
        "or to target a specific .sln/.slnx by path. Use list_analysis_tools for the full catalogue.";

    using var host = Host.CreateDefaultBuilder(args)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new FileLoggerProvider(logPath));
            logging.SetMinimumLevel(LogLevel.Debug);
        })
        .ConfigureServices(services =>
        {
            services.AddMcpServer(options =>
                {
                    options.ServerInstructions = ServerInstructions;
                })
                .WithStdioServerTransport()
                .WithToolsFromAssembly()
                .WithToolCallLogging();
        })
        .Build();

    var logger = host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("VisualMCP.Startup");

    // Eagerly auto-load the solution in the background so it is usually ready by
    // the time the first tool call arrives. Tools also auto-load on demand, so
    // this is purely a latency optimisation, not a correctness requirement.
    _ = Task.Run(async () =>
    {
        try
        {
            var path = RoslynWorkspaceService.DiscoverSolutionPath();
            if (path is null)
            {
                logger.LogInformation(
                    "No solution auto-discovered from working directory '{Cwd}'. " +
                    "Tools will prompt for load_solution.", Directory.GetCurrentDirectory());
                return;
            }

            logger.LogInformation("Auto-loading discovered solution: {Path}", path);
            var result = await RoslynWorkspaceService.Instance.LoadSolutionAsync(path);
            logger.LogInformation("Auto-loaded solution with {Count} project(s).",
                result.Solution.Projects.Count());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background solution auto-load failed; tools will retry on demand.");
        }
    });

    await host.RunAsync();
}
