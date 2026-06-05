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
        "- Compiler errors/warnings for the whole solution -> get_diagnostics.\n" +
        "- Run the app or the tests -> run_project (launches 'dotnet run' with a timeout), run_tests.\n" +
        "- Run a build/export/packaging script or any shell command — PowerShell, pwsh, cmd or bash (incl. nested shells, pipes, grep/sed, redirection, or commands too long for the normal shell) -> run_command.\n" +
        "- Free a locked build output / restart the server (e.g. a 'file in use' error when rebuilding) -> stop_server.\n" +
        "- Check the local toolchain (dotnet SDKs/runtimes, Visual Studio & MSVC cl.exe via vswhere, OS, and the size of given files/libraries) -> get_environment_info.\n" +
        "- Manage NuGet packages: versions & version conflicts -> list_nuget_packages; available upgrades -> check_nuget_updates; known CVEs/advisories -> check_nuget_vulnerabilities; find a package's DLLs in the local cache -> locate_nuget_package; add a package to a project -> add_nuget_package.\n" +
        "- Inspect a compiled .NET assembly's public API (types, members, signatures) without running it -> inspect_assembly (great with locate_nuget_package for third-party DLLs).\n" +
        "- Add/remove projects to/from the solution (.sln/.slnx) -> add_projects_to_solution / remove_projects_from_solution.\n" +
        "- Git: inspect state -> git_status / git_log / git_diff; stage -> git_stage; commit -> git_commit; new branch -> git_create_branch. (No push/force — use run_command for those.)\n" +
        "- Check if the solution/project compiles and get structured errors/warnings -> build_project " +
        "(runs 'dotnet build', works even while the app is running).\n" +
        "- Extract errors from a native/C++ or MSBuild log file (handles UTF-16) -> extract_build_log_errors.\n" +
        "- Check Docker engine availability and running containers -> docker_status.\n" +
        "- List a folder's contents/sizes (build output, repo root, a library dir) -> list_directory (read-only; filter by glob/name, optional recurse).\n" +
        "- Call a REST API endpoint of a running app -> http_invoke (any HTTP method, custom headers, JSON body).\n" +
        "- Test a SignalR hub (connect, subscribe, invoke, drain events) -> signalr_connect / signalr_subscribe / signalr_invoke / signalr_events / signalr_disconnect.\n\n" +
        "WORKFLOW:\n" +
        "- When you START working on a solution, first call read_project_docs to read its README/Markdown/" +
        "docs (indexed automatically when the solution opens) and understand the project before reading code.\n" +
        "- When a task has MULTIPLE steps, call create_task_checklist at the start to write a Markdown " +
        "Task|Done table, then update_task_checklist to tick steps off as you complete them.\n\n" +
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
