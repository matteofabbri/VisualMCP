using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Modernization;

namespace VisualMCP.Tools.Modernization;

[McpServerToolType]
public static class AspireTool
{
    [McpServerTool(Name = "aspire"), Description(
        "Install and scaffold .NET Aspire on demand. action='status' reports the SDK band, whether the " +
        "Aspire templates and the 'aspire' workload are installed, and which projects already are an " +
        "AppHost, ServiceDefaults or Aspire consumer. action='install' installs Aspire.ProjectTemplates " +
        "(no elevation; the workload is only needed on .NET 8 SDKs and is installed automatically there or " +
        "with includeWorkload=true). action='add' scaffolds the solution: it creates the AppHost and " +
        "ServiceDefaults projects, adds them to the .sln, references every runnable project from the " +
        "AppHost, writes the AddProject<T>(\"name\") lines, wires AddServiceDefaults()/MapDefaultEndpoints() " +
        "into each service, and compiles the AppHost — reverting the Program.cs wiring if it does not build. " +
        "Everything is idempotent: an existing AppHost or template is reported, not recreated. Missing " +
        "templates are installed automatically when add runs with installIfMissing=true (the default).")]
    public static Task<object> Aspire(
        IAspireService aspire,
        [Description("'status' (default), 'install' or 'add'.")] string action = "status",
        [Description("For action='add': the AppHost project name (default '<solution>.AppHost').")] string? appHostName = null,
        [Description("For action='add': service projects to register in the AppHost (names or .csproj paths). Omit for every runnable project.")] string[]? projects = null,
        [Description("For action='add': also create and wire the ServiceDefaults project (default true).")] bool serviceDefaults = true,
        [Description("For action='add': install the Aspire templates when they are missing (default true).")] bool installIfMissing = true,
        [Description("For action='add': build the AppHost afterwards and revert the Program.cs wiring when it fails (default true).")] bool verify = true,
        [Description("For action='install': also install the 'aspire' workload (only needed on .NET 8 SDKs; may require elevation).")] bool includeWorkload = false,
        [Description("For action='install': install even when the templates are already present.")] bool force = false,
        [Description("Timeout in seconds for each dotnet command (default 300 — template install hits NuGet).")] int timeoutSeconds = 300)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "status" => aspire.StatusAsync(timeoutSeconds),
            "install" => aspire.InstallAsync(includeWorkload, force, timeoutSeconds),
            "add" => aspire.AddAsync(appHostName, projects, serviceDefaults, installIfMissing, verify, timeoutSeconds),
            _ => Task.FromResult<object>(new
            {
                error = $"Unknown action '{action}'. Supported: status, install, add.",
            }),
        };
    }
}
