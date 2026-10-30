using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitRemoteTool
{
    [McpServerTool(Name = "git_remote"), Description(
        "Inspect and configure remotes: list (default, name + url + kind), add a remote with a url, remove one, or " +
        "change its url. Use this INSTEAD OF shell 'git remote'.")]
    public static Task<object> GitRemote(
        IGitService git,
        [Description("list | add | remove | set-url")] string action = "list",
        [Description("Remote name, e.g. 'origin'.")] string? name = null,
        [Description("Remote url (required for add and set-url).")] string? url = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.RemoteAsync(action, name, url, repoPath);
}
