using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.CSharp.Packages;

namespace VisualMCP.Tools.CSharp.Packages;

[McpServerToolType]
public static class ListNuGetPackagesTool
{
    [McpServerTool(Name = "list_nuget_packages"), Description(
        "List the NuGet packages of the solution (or one project) with their requested and resolved " +
        "versions per target framework, and detect VERSION CONFLICTS — the same package pinned to " +
        "different resolved versions across projects. Use this INSTEAD OF reading .csproj files by hand: " +
        "it reflects the real restored graph from 'dotnet list package'. Set includeTransitive=true to " +
        "also list indirect dependencies. The solution auto-loads on first use; the project must be restored " +
        "(run build_project once if it reports it needs restoring).")]
    public static Task<object> ListNuGetPackages(
        [Description("Optional: restrict to a single project by name. Omit for the whole solution.")] string? projectName = null,
        [Description("Also include transitive (indirect) packages (default: false).")] bool includeTransitive = false,
        [Description("Timeout in seconds (default: 120).")] int timeoutSeconds = 120)
        => NuGetPackagesImpl.ListAsync(projectName, includeTransitive, timeoutSeconds);
}
