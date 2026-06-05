using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Update;

namespace VisualMCP.Tools.Update;

[McpServerToolType]
public static class CheckForUpdateTool
{
    [McpServerTool(Name = "check_for_update"), Description(
        "Check GitHub Releases for a newer build of VisualMCP: returns the latest release tag, publish date, " +
        "downloadable assets, the current installed version and install directory. Read-only — use self_update " +
        "to actually install.")]
    public static Task<object> CheckForUpdate(
        [Description("Optional: GitHub repo 'owner/name' to check (defaults to the project's release repo).")] string? repo = null)
        => SelfUpdateImpl.CheckAsync(repo);
}
