using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Modernization;

namespace VisualMCP.Tools.Modernization;

[McpServerToolType]
public static class UpdateFrameworkTool
{
    [McpServerTool(Name = "update_framework"), Description(
        "Move the solution's projects to a newer .NET target framework. Edits only the value of " +
        "TargetFramework/TargetFrameworks inside each project file (and Directory.Build.props when it " +
        "holds one), keeps platform suffixes (net8.0-windows → net10.0-windows), and leaves netstandard / " +
        "net4x targets alone because they exist to keep older consumers working. targetFramework accepts " +
        "'net10.0', '10.0' or 'auto' (the newest installed SDK band). Optionally keeps an existing " +
        "global.json in sync (updateGlobalJson). Preview by default: dryRun=true. With dryRun=false the " +
        "whole solution is built afterwards and every edited file is restored if the build fails, so a " +
        "failed bump never leaves a half-upgraded tree. Use update_solution afterwards to bring NuGet " +
        "packages to versions that match.")]
    public static Task<object> UpdateFramework(
        ISolutionUpgradeService upgrade,
        [Description("Target framework: 'net10.0', '10.0' or 'auto' for the newest installed SDK band (default).")] string? targetFramework = "auto",
        [Description("Only these solution projects (names). Omit for every project.")] string[]? projectNames = null,
        [Description("Preview only — nothing is written (default true).")] bool dryRun = true,
        [Description("Build the solution afterwards and restore every edited file when it fails (default true).")] bool buildGate = true,
        [Description("Keep an existing global.json's sdk.version in sync (default true; a missing global.json is never created by this flag).")] bool updateGlobalJson = true,
        [Description("Create a global.json when the solution has none (default false).")] bool createGlobalJson = false,
        [Description("Timeout in seconds for the build (default 900).")] int timeoutSeconds = 900,
        CancellationToken cancellationToken = default)
        => upgrade.UpdateFrameworkAsync(
            targetFramework, projectNames, dryRun, buildGate, updateGlobalJson, createGlobalJson, timeoutSeconds, cancellationToken);
}
