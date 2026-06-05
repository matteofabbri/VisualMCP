using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Update;

namespace VisualMCP.Tools.Update;

[McpServerToolType]
public static class SelfUpdateTool
{
    [McpServerTool(Name = "self_update"), Description(
        "Update VisualMCP to the latest GitHub release: downloads the matching release asset (default the " +
        "win-x64 .zip), stages it next to the install, then exits the server so a detached helper swaps in the " +
        "new version — Claude Code relaunches the updated server (brief disconnect). Run check_for_update first " +
        "to see what's available.")]
    public static Task<object> SelfUpdate(
        [Description("Optional: GitHub repo 'owner/name' to update from (defaults to the project's release repo).")] string? repo = null,
        [Description("Substring to pick the release asset (default 'win-x64'); the first matching .zip is used.")] string? assetPattern = null)
        => SelfUpdateImpl.UpdateAsync(repo, assetPattern);
}
