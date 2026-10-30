using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubReleaseTool
{
    [McpServerTool(Name = "gh_release"), Description(
        "Work with releases: list (parsed), view one by tag, create (annotated tag + notes, draft/prerelease, " +
        "optional asset paths), upload assets to an existing release, or delete (confirm=true). " +
        "Use this INSTEAD OF shell 'gh release ...'.")]
    public static Task<object> GitHubRelease(
        IGitHubService github,
        [Description("list | view | create | upload | delete")] string action = "list",
        [Description("Tag, e.g. 'v1.2.0' (view/create/upload/delete).")] string? tag = null,
        [Description("Release title (create).")] string? title = null,
        [Description("Release notes (create); omit to generate them from the commits.")] string? notes = null,
        [Description("Asset path(s) to attach (create/upload), space-separated for several.")] string? asset = null,
        [Description("Create as a draft.")] bool draft = false,
        [Description("Mark as a pre-release.")] bool prerelease = false,
        [Description("Must be true for delete.")] bool confirm = false,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null)
        => github.ReleaseAsync(action, tag, title, notes, asset, draft, prerelease, confirm, repo);
}
