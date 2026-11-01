using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class InstallerAuthoringTool
{
    [McpServerTool(Name = "installer_project"), Description(
        "Write and verify an INSTALLER for a project. kind=wix scaffolds a WiX v4/v5 project under " +
        "<project>/Installer (<Name>.wixproj plus Package.wxs: an MSI that packs the publish folder named by " +
        "PublishDir, a MajorUpgrade and a stable UpgradeCode derived from the project name); " +
        "kind=clickonce writes Properties/PublishProfiles/ClickOnce.pubxml with the ClickOnce keys " +
        "(PublishProtocol, PublishUrl, InstallUrl, ApplicationVersion, bootstrapper). action=scaffold is " +
        "previewed unless dryRun=false and never overwrites a file that is already there; action=verify " +
        "actually builds what was scaffolded — 'dotnet build' on the .wixproj (the WiX SDK restores from " +
        "NuGet on the first run) or 'dotnet publish -p:PublishProfile' for ClickOnce — and returns its exit " +
        "code and output. MSIX is not generated here: it needs the Windows packaging tools, not an SDK.")]
    public static Task<InstallerAuthoringResult> InstallerProject(
        IInstallerAuthoringService installers,
        [Description("scaffold (write the files) or verify (build what was scaffolded).")] string action,
        [Description("The project the installer packages (exact name from the loaded solution).")] string projectName,
        [Description("wix (an MSI) or clickonce (a self-updating launcher).")] string kind,
        [Description("Override the generated name: the .wixproj name for wix, the profile name for clickonce.")]
        string? name = null,
        [Description("For scaffold: preview only (default true). Pass false to write the files.")]
        bool dryRun = true,
        [Description("For verify: timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => action.Trim().ToLowerInvariant() switch
        {
            "scaffold" => installers.ScaffoldAsync(projectName, kind, name, dryRun),
            "verify" => installers.VerifyAsync(projectName, kind, name, timeoutSeconds),
            _ => Task.FromResult(new InstallerAuthoringResult
            {
                Kind = kind,
                Error = $"Unknown action '{action}': use scaffold or verify.",
            }),
        };
}
