using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;
using Snitch.Languages.CSharp.Packages;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class ProjectPropertiesTool
{
    [McpServerTool(Name = "project_properties"), Description(
        "The evaluated MSBuild view of a project and the write side for one property. action=get runs " +
        "'dotnet msbuild -getProperty/-getItem' and returns what MSBuild computes (including what " +
        "Directory.Build.props and the SDK contribute), not the literal file text: pass properties=['Nullable',…] " +
        "and/or items=['PackageReference',…], with Configuration/Platform when the answer depends on them. " +
        "action=set / action=unset write one property into the .csproj (scope=project) or the nearest " +
        "Directory.Build.props (scope=props), with a build gate that restores the file when the solution stops " +
        "compiling; dryRun defaults to true and every write is recorded in the edit history. Use this instead of " +
        "reading the csproj by hand when the answer depends on conditions or imports.")]
    public static Task<object> ProjectProperties(
        IProjectPropertiesService svc,
        [Description("get (default) | set | unset.")] string action = "get",
        [Description("Project name from the loaded solution. Omit when the solution has one project.")] string? projectName = null,
        [Description("A .csproj path, when the project is not in the solution or the name is ambiguous.")] string? projectPath = null,
        [Description("For get: MSBuild property names to evaluate. Omit for the usual set (TargetFramework, Nullable, OutputType, …).")] string[]? properties = null,
        [Description("For get: MSBuild item names to evaluate, e.g. PackageReference, Compile, ProjectReference.")] string[]? items = null,
        [Description("For get: the Configuration to evaluate with (e.g. Debug, Release).")] string? configuration = null,
        [Description("For get: the Platform to evaluate with (e.g. AnyCPU, x64).")] string? platform = null,
        [Description("For set/unset: the MSBuild property name, e.g. Nullable, LangVersion, TreatWarningsAsErrors.")] string? property = null,
        [Description("For set: the value to write.")] string? value = null,
        [Description("For set/unset: 'project' (the .csproj) or 'props' (the nearest Directory.Build.props). Default: project.")] string scope = "project",
        [Description("For set/unset: preview only. Default true.")] bool dryRun = true,
        [Description("For set/unset: build the solution afterwards and restore the file if it stops compiling. Default true.")] bool buildGate = true,
        [Description("Timeout in seconds (default 120; the build gate uses it too).")] int timeoutSeconds = 120)
        => action.Trim().ToLowerInvariant() switch
        {
            "get" => svc.GetAsync(projectName, projectPath, properties, items, configuration, platform, timeoutSeconds),
            "set" => string.IsNullOrWhiteSpace(property)
                ? Task.FromResult<object>(new { error = "A property name is required for action=set." })
                : svc.SetAsync(projectName, projectPath, property, value, remove: false, scope, dryRun, buildGate, timeoutSeconds),
            "unset" => string.IsNullOrWhiteSpace(property)
                ? Task.FromResult<object>(new { error = "A property name is required for action=unset." })
                : svc.SetAsync(projectName, projectPath, property, null, remove: true, scope, dryRun, buildGate, timeoutSeconds),
            _ => Task.FromResult<object>(new { error = $"Unknown action '{action}'. Use get, set or unset." }),
        };
}

[McpServerToolType]
public static class NuGetSourcesTool
{
    [McpServerTool(Name = "nuget_sources"), Description(
        "The NuGet package sources of the repository, through the .NET CLI: list them with their enabled state " +
        "and URL, add/update/remove one, enable or disable one. Credentials are passed to NuGet and never echoed " +
        "back (the reported command has the password redacted; NuGet encrypts it by default, and " +
        "storePasswordInClearText=true is only for a feed that requires it). Use this instead of editing " +
        "NuGet.Config by hand or shipping a password in a plain-text file.")]
    public static Task<object> NuGetSources(
        INuGetSourcesService svc,
        [Description("list (default) | add | update | remove | enable | disable.")] string action = "list",
        [Description("Source name, e.g. nuget.org or a private feed name.")] string? name = null,
        [Description("For add/update: the source URL (or a local folder).")] string? url = null,
        [Description("Optional username for an authenticated feed.")] string? username = null,
        [Description("Optional password for an authenticated feed; never echoed back.")] string? password = null,
        [Description("Store the password in clear text instead of NuGet's encrypted form. Only for feeds that require it.")] bool storePasswordInClearText = false,
        [Description("A specific NuGet.Config to operate on (--configfile). Omit for the repository's config chain.")] string? configFile = null,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => action.Trim().ToLowerInvariant() switch
        {
            "list" => svc.ListAsync(configFile, timeoutSeconds),
            "add" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A source name is required for action=add." })
                : svc.AddAsync(name, url ?? "", username, password, storePasswordInClearText, configFile, timeoutSeconds),
            "update" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A source name is required for action=update." })
                : svc.UpdateAsync(name, url, username, password, storePasswordInClearText, configFile, timeoutSeconds),
            "remove" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A source name is required for action=remove." })
                : svc.RemoveAsync(name, configFile, timeoutSeconds),
            "enable" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A source name is required for action=enable." })
                : svc.ToggleAsync(name, enable: true, configFile, timeoutSeconds),
            "disable" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A source name is required for action=disable." })
                : svc.ToggleAsync(name, enable: false, configFile, timeoutSeconds),
            _ => Task.FromResult<object>(new { error = $"Unknown action '{action}'. Use list, add, update, remove, enable or disable." }),
        };
}

[McpServerToolType]
public static class BinlogAnalysisTool
{
    [McpServerTool(Name = "analyze_binlog"), Description(
        "What an MSBuild binary log actually did: the errors and warnings with their project, file and line, " +
        "the duration of every project and every target, which target dominates the wall clock, and the project " +
        "graph the build walked. Pass binlogPath to read an existing '.binlog'; omit it to run 'dotnet build -bl' " +
        "first (the log is written under .visualMCP/binlogs and can be analysed again later). A target's duration " +
        "includes the projects it builds, so compare it with its children in the graph before calling it the problem.")]
    public static Task<object> AnalyzeBinlog(
        IBinlogAnalysisService svc,
        [Description("Path to an existing .binlog. Omit to build the solution (or projectName) and analyse that log.")] string? binlogPath = null,
        [Description("Build only this project (when binlogPath is omitted).")] string? projectName = null,
        [Description("Build configuration for a generated log (default: Debug).")] string configuration = "Debug",
        [Description("How many projects, targets, errors and warnings to return (default: 25).")] int top = 25,
        [Description("Timeout in seconds for the build (default 300).")] int timeoutSeconds = 300)
        => svc.AnalyzeAsync(binlogPath, projectName, configuration, top, timeoutSeconds);
}

[McpServerToolType]
public static class RunConfigurationTool
{
    [McpServerTool(Name = "run_config"), Description(
        "Save and list the ways a project is launched, per solution, in .visualMCP/run-configs.json: the project, " +
        "the application arguments, the build configuration and the environment variables. Then run_project " +
        "configName='<name>' applies them instead of retyping the arguments on every call. The file is the source " +
        "of truth and can be committed or edited by hand; a value passed explicitly to run_project still wins. " +
        "action=list | save | delete.")]
    public static Task<object> RunConfig(
        IRunConfigurationService svc,
        [Description("list (default) | save | delete.")] string action = "list",
        [Description("Configuration name, e.g. 'api-dev'.")] string? name = null,
        [Description("For save: the project to launch. Omit to let run_project auto-select the only runnable project.")] string? project = null,
        [Description("For save: the arguments passed to the app (after '--').")] string? args = null,
        [Description("For save: the build configuration (e.g. Debug, Release).")] string? configuration = null,
        [Description("For save: pass --no-build.")] bool noBuild = false,
        [Description("For save: environment variables for the launched process, e.g. {\"ASPNETCORE_ENVIRONMENT\":\"Development\"}.")] Dictionary<string, string>? environment = null)
        => action.Trim().ToLowerInvariant() switch
        {
            "list" => svc.ListAsync(),
            "save" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A configuration name is required for action=save." })
                : svc.SaveAsync(name, project, args, configuration, noBuild, environment),
            "delete" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "A configuration name is required for action=delete." })
                : svc.DeleteAsync(name),
            _ => Task.FromResult<object>(new { error = $"Unknown action '{action}'. Use list, save or delete." }),
        };
}

[McpServerToolType]
public static class LaunchSettingsTool
{
    [McpServerTool(Name = "launch_settings"), Description(
        "Properties/launchSettings.json as data: list the profiles (command name, applicationUrl, arguments, " +
        "environment variables), add or update one, remove one. The rest of the document — $schema, iisSettings, " +
        "profiles nobody touched — is preserved, and every write is recorded in the edit history. This is the " +
        "profile 'dotnet run --launch-profile <name>' and the IDE launch with; previewed by default.")]
    public static Task<object> LaunchSettings(
        ILaunchSettingsService svc,
        [Description("list (default) | set | remove.")] string action = "list",
        [Description("Project name from the loaded solution.")] string? projectName = null,
        [Description("A .csproj path or project folder, when the project is not in the solution.")] string? projectPath = null,
        [Description("The profile to read (with action=list), create/update (set) or delete (remove).")] string? profileName = null,
        [Description("For set: the commandName (Project, IIS Express, Executable, …). Defaults to Project on a new profile.")] string? commandName = null,
        [Description("For set: the applicationUrl, e.g. https://localhost:7001;http://localhost:5001.")] string? applicationUrl = null,
        [Description("For set: the command-line arguments the profile passes to the app.")] string? commandLineArgs = null,
        [Description("For set: environment variables to add or overwrite, e.g. {\"ASPNETCORE_ENVIRONMENT\":\"Development\"}.")] Dictionary<string, string>? environmentVariables = null,
        [Description("For set: replace the existing environmentVariables object instead of merging into it.")] bool replaceEnvironmentVariables = false,
        [Description("Preview only (default true).")] bool dryRun = true,
        [Description("For remove: must be true to delete the profile.")] bool confirm = false)
        => action.Trim().ToLowerInvariant() switch
        {
            "list" => svc.ListAsync(projectName, projectPath, profileName),
            "set" => string.IsNullOrWhiteSpace(profileName)
                ? Task.FromResult<object>(new { error = "A profileName is required for action=set." })
                : svc.SetAsync(projectName, projectPath, profileName, commandName, applicationUrl, commandLineArgs,
                    environmentVariables, replaceEnvironmentVariables, dryRun),
            "remove" => string.IsNullOrWhiteSpace(profileName)
                ? Task.FromResult<object>(new { error = "A profileName is required for action=remove." })
                : svc.RemoveAsync(projectName, projectPath, profileName, confirm, dryRun),
            _ => Task.FromResult<object>(new { error = $"Unknown action '{action}'. Use list, set or remove." }),
        };
}

[McpServerToolType]
public static class PublishProfilesTool
{
    [McpServerTool(Name = "publish_profile"), Description(
        "Properties/PublishProfiles/*.pubxml as data: list the profiles with the properties each one sets, read " +
        "one in full, create or update one, delete one. The keys are the MSBuild properties 'dotnet publish " +
        "-p:PublishProfile=<name>' reads (PublishDir, RuntimeIdentifier, SelfContained, PublishSingleFile, …), so " +
        "this is how a filesystem or ClickOnce publish target is authored without an IDE. Previewed by default; " +
        "deleting a profile needs confirm=true.")]
    public static Task<object> PublishProfile(
        IPublishProfilesService svc,
        [Description("list (default) | set | delete.")] string action = "list",
        [Description("Project name from the loaded solution.")] string? projectName = null,
        [Description("A .csproj path or project folder, when the project is not in the solution.")] string? projectPath = null,
        [Description("The profile name (with or without '.pubxml').")] string? profileName = null,
        [Description("For set: the properties to write, e.g. {\"PublishDir\":\"bin/publish\",\"RuntimeIdentifier\":\"win-x64\"}.")] Dictionary<string, string>? properties = null,
        [Description("For set: create the .pubxml when it does not exist. Default true.")] bool createIfMissing = true,
        [Description("Preview only (default true).")] bool dryRun = true,
        [Description("For delete: must be true to remove the profile file.")] bool confirm = false)
        => action.Trim().ToLowerInvariant() switch
        {
            "list" => svc.ListAsync(projectName, projectPath, profileName),
            "set" => string.IsNullOrWhiteSpace(profileName)
                ? Task.FromResult<object>(new { error = "A profileName is required for action=set." })
                : svc.SetAsync(projectName, projectPath, profileName, properties ?? [], createIfMissing, dryRun),
            "delete" => string.IsNullOrWhiteSpace(profileName)
                ? Task.FromResult<object>(new { error = "A profileName is required for action=delete." })
                : svc.DeleteAsync(projectName, projectPath, profileName, confirm),
            _ => Task.FromResult<object>(new { error = $"Unknown action '{action}'. Use list, set or delete." }),
        };
}
