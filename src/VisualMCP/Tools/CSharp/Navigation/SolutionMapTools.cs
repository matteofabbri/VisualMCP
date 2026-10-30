using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;
using Snitch.Languages.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class SolutionMapTool
{
    [McpServerTool(Name = "solution_map"), Description(
        "The map of the CODEBASE for an agent that has just landed in it: the projects and what each one " +
        "is (kind, test project or not, document and type counts, project references), the namespaces that " +
        "carry the most members with their largest types, the entry points (Main / top-level statements), " +
        "and the repository's own Markdown with the first heading of each page. Syntax-level on purpose: " +
        "one call, no compilation. For a symbol-level view use reference_index or find_symbol; for what the " +
        "SERVER can do (not this codebase) use capability_report. Read-only.")]
    public static Task<object> SolutionMap(
        ISolutionMapService map,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("How many namespaces to return, by member count (default 20).")] int maxAreas = 20,
        [Description("How many of each namespace's largest types to list (default 10).")] int maxTypes = 10)
        => map.MapAsync(projectName, maxAreas, maxTypes);
}

[McpServerToolType]
public static class CapabilityReportTool
{
    [McpServerTool(Name = "capability_report"), Description(
        "What the SERVER can do, read from the running build: every tool it advertises grouped by area (with " +
        "the live count), the rule catalogues it carries (context actions, XAML and Razor rule ids, style " +
        "rewrites, inspection packs) and the language layers with the type that implements each one. Use it " +
        "instead of the documentation tables when the counts matter — these cannot drift from the build. " +
        "Read-only.")]
    public static object CapabilityReport(VisualMCP.Analysis.ICapabilityReportService report) => report.Report();
}

[McpServerToolType]
public static class BuildPackagingTools
{
    [McpServerTool(Name = "dotnet_publish"), Description(
        "Publish a project (self-contained / framework-dependent, RID-specific, single-file, to an output " +
        "folder) and return the command, exit code and output as structured data. Read-only on sources: it " +
        "writes only the publish output.")]
    public static Task<object> DotnetPublish(
        IBuildPackagingService svc,
        [Description("The project (exact name from the solution). Omit when the solution has one.")] string? projectName = null,
        [Description("Release (default) or Debug.")] string configuration = "Release",
        [Description("Runtime identifier, e.g. win-x64, linux-x64.")] string? runtime = null,
        [Description("With a runtime: self-contained (default false).")] bool selfContained = false,
        [Description("Publish as a single file.")] bool singleFile = false,
        [Description("Output folder.")] string? output = null,
        [Description("Pass --no-build.")] bool noBuild = false,
        [Description("Timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => svc.PublishAsync(projectName, configuration, runtime, selfContained, singleFile, output, noBuild, timeoutSeconds);

    [McpServerTool(Name = "add_project_reference"), Description(
        "Add a <ProjectReference> from one project to another (or to a .csproj path) with 'dotnet add " +
        "reference', then the workspace reloads. Structured result.")]
    public static Task<object> AddProjectReference(
        IBuildPackagingService svc,
        [Description("The project that gains the reference.")] string projectName,
        [Description("Target: a project name from the solution, or a .csproj path.")] string reference,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => svc.AddProjectReferenceAsync(projectName, reference, timeoutSeconds);

    [McpServerTool(Name = "remove_project_reference"), Description(
        "Remove a <ProjectReference> with 'dotnet remove reference'. Structured result.")]
    public static Task<object> RemoveProjectReference(
        IBuildPackagingService svc,
        [Description("The project that loses the reference.")] string projectName,
        [Description("Target: a project name from the solution, or a .csproj path.")] string reference,
        [Description("Timeout in seconds (default 120).")] int timeoutSeconds = 120)
        => svc.RemoveProjectReferenceAsync(projectName, reference, timeoutSeconds);

    [McpServerTool(Name = "nuget_pack"), Description(
        "Pack a project into a NuGet package ('dotnet pack'), optionally with a version and an output folder. " +
        "Structured result: the package path is in the output.")]
    public static Task<object> NugetPack(
        IBuildPackagingService svc,
        [Description("The project (exact name). Omit when the solution has one.")] string? projectName = null,
        [Description("Release (default) or Debug.")] string configuration = "Release",
        [Description("Output folder.")] string? output = null,
        [Description("Package version (PackageVersion).")] string? version = null,
        [Description("Pass --no-build.")] bool noBuild = false,
        [Description("Timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => svc.PackAsync(projectName, configuration, output, version, noBuild, timeoutSeconds);

    [McpServerTool(Name = "nuget_push"), Description(
        "Push a .nupkg to a feed ('dotnet nuget push'). The API key is used but never echoed back. This " +
        "publishes the package: there is no preview.")]
    public static Task<object> NugetPush(
        IBuildPackagingService svc,
        [Description("Path to the .nupkg.")] string packagePath,
        [Description("Feed URL or configured source name (default: the configured one).")] string? source = null,
        [Description("API key, when the feed needs one.")] string? apiKey = null,
        [Description("Timeout in seconds (default 180).")] int timeoutSeconds = 180)
        => svc.PushAsync(packagePath, source, apiKey, timeoutSeconds);
}
