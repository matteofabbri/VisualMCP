using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Modernization;

namespace VisualMCP.Tools.Modernization;

[McpServerToolType]
public static class UpdateSolutionTool
{
    [McpServerTool(Name = "update_solution"), Description(
        "Bring the whole solution up to date in one gated operation: optionally bump the target framework " +
        "(targetFramework='auto' or 'net10.0'), then raise every outdated top-level NuGet package to its " +
        "latest version — in the project files or, when Central Package Management is in use, in the " +
        "Directory.Packages.props rows. Version attributes are edited in place (a bump looks like a bump, " +
        "not a rewritten file), then the solution is built and, if it fails, every changed file is restored. " +
        "Preview by default: dryRun=true. Use omit targetFramework to update only the packages. Prefer this " +
        "over one update_nuget_package call per package when the goal is 'modernise this solution'.")]
    public static Task<object> UpdateSolution(
        ISolutionUpgradeService upgrade,
        [Description("Also bump the target framework: 'net10.0', '10.0' or 'auto' for the newest installed SDK band. Omit to update packages only.")] string? targetFramework = null,
        [Description("Only these solution projects (names). Omit for every project.")] string[]? projectNames = null,
        [Description("Consider pre-release package versions as upgrades (default false).")] bool includePrerelease = false,
        [Description("Preview only — nothing is written (default true).")] bool dryRun = true,
        [Description("Build the solution afterwards and restore every edited file when it fails (default true).")] bool buildGate = true,
        [Description("When the framework is bumped, keep an existing global.json's sdk.version in sync (default true).")] bool updateGlobalJson = true,
        [Description("Create a global.json when the solution has none (default false).")] bool createGlobalJson = false,
        [Description("Timeout in seconds for 'dotnet list package' and for the build (default 900).")] int timeoutSeconds = 900,
        CancellationToken cancellationToken = default)
        => upgrade.UpdateSolutionAsync(
            targetFramework, projectNames, includePrerelease, dryRun, buildGate, updateGlobalJson, createGlobalJson,
            timeoutSeconds, cancellationToken);
}
