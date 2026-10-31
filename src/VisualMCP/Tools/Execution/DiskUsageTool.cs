using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class DiskUsageTool
{
    [McpServerTool(Name = "disk_usage"), Description(
        "Where the disk went, and how to keep the next build off the system drive. action=report (default) " +
        "measures every folder a .NET session grows — TEMP/TMP (MSBuild temp, testhost, NuGet scratch), the " +
        "NuGet global-packages/http/plugins caches, the .NET CLI home, the VisualMCP tool installs (Ghidra, " +
        "netcoredbg), the MCP log, the solution store and, with deep=true, the workspace's bin/obj — each with " +
        "its absolute path, bytes, file count, risk and the clean target that removes it, plus findings that " +
        "name the cause and the fix. action=clean deletes ONE known target (dry run by default; a real deletion " +
        "needs dryRun=false AND confirm=true; temp targets honour olderThanHours so an in-flight build is left " +
        "alone). action=guard redirects TEMP/TMP, NUGET_PACKAGES, NUGET_HTTP_CACHE_PATH, DOTNET_CLI_HOME and " +
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR under a root on another drive, applies them to this process so every " +
        "child (dotnet build/test, git, shells) inherits them, and persists the policy in " +
        "<workspace>/.visualMCP/disk-policy.json for the next start. Use it BEFORE a long build/test session " +
        "on a machine whose system drive is short of space, and AFTER one to see what grew.")]
    public static Task<object> DiskUsage(
        IDiskUsageService disk,
        [Description("'report' (default) measures the folders, 'clean' removes one known target, 'guard' redirects TEMP/NuGet/.NET CLI state to another drive.")] string action = "report",
        [Description("Extra absolute paths to measure in a report (report only; never deleted by this tool).")] string[]? roots = null,
        [Description("Also break each folder into its largest children and include the workspace's bin/obj folders (slower).")] bool deep = false,
        [Description("How many children per folder a deep report returns (1-50, default 10).")] int top = 10,
        [Description("Clean only: target ids to delete. Default [\"temp-stale\"]. Also 'all-safe' (caches and temp only) or 'all-reclaimable' (adds NuGet packages, tool installs, decompiled code, bin/obj).")] string[]? targets = null,
        [Description("Clean only: temp entries younger than this many hours are kept (default 24; 0 means every entry).")] double olderThanHours = 24,
        [Description("Clean only: preview by default. Nothing is deleted while this is true.")] bool dryRun = true,
        [Description("Clean only: must be true together with dryRun=false, or the call is refused.")] bool confirm = false,
        [Description("Guard only: false (default) shows the variables that would be set; true applies them and persists the policy.")] bool apply = false,
        [Description("Guard: the root on the drive that should hold temp and caches (e.g. D:\\vmcp-tmp). Clean: overrides the temp root for temp-stale/temp-all, so a specific folder can be cleaned.")] string? root = null,
        [Description("Report budget in seconds (5-300, default 20). A folder that runs out is reported partial instead of holding the call.")] int timeoutSeconds = 20,
        CancellationToken cancellationToken = default)
        => disk.RunAsync(
            action: action,
            roots: roots,
            deep: deep,
            top: top,
            targets: targets,
            olderThanHours: olderThanHours,
            dryRun: dryRun,
            confirm: confirm,
            apply: apply,
            root: root,
            timeoutSeconds: timeoutSeconds,
            includeWellKnown: true,
            cancellationToken: cancellationToken);
}
