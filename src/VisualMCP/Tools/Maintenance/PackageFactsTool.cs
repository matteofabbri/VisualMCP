using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class PackageFactsTool
{
    [McpServerTool(Name = "package_facts"), Description(
        "The NuGet facts the build already resolved, stored in .visualMCP/visualmcp.db: every package a project " +
        "actually uses, per target framework, with the dependency edges between them. action=refresh re-reads each " +
        "project's obj/project.assets.json (no restore, no network); action=list shows the packages with their resolved " +
        "version and frameworks (filter by project, id substring, direct only); action=dependencies shows the edges, or " +
        "with packageId the packages that pull it in. These are the resolved versions, i.e. what the compiler sees, not " +
        "what the csproj asks for.")]
    public static Task<object> PackageFacts(
        IPackageFactsService packages,
        [Description("refresh | list | dependencies (default: list).")] string action = "list",
        [Description("Only this project.")] string? project = null,
        [Description("For list: id substring. For dependencies: the package whose edges you want.")] string? packageId = null,
        [Description("For list: only packages the project references directly.")] bool directOnly = false,
        [Description("How many rows to return (default 500).")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "refresh" => packages.RefreshAsync(project, cancellationToken),
            "list" => Task.FromResult(packages.List(project, packageId, directOnly, limit)),
            "dependencies" => Task.FromResult(packages.Dependencies(project, packageId, limit)),
            _ => Task.FromResult<object>(new { error = "action must be refresh, list or dependencies." }),
        };
    }
}
