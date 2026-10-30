using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubRepoTool
{
    [McpServerTool(Name = "gh_repo"), Description(
        "Manage GitHub repositories: list (yours, parsed), view one (name, visibility, default branch, stars, url), " +
        "create (private by default; directory+push publishes an existing folder), fork, clone, edit (description, " +
        "visibility) or delete (requires confirm=true). Use this INSTEAD OF shell 'gh repo ...'; the raw 'github' " +
        "tool remains available for anything else.")]
    public static Task<object> GitHubRepo(
        IGitHubService github,
        [Description("list | view | create | fork | clone | edit | delete")] string action = "list",
        [Description("For create: repository name (optionally owner/name).")] string? name = null,
        [Description("Description used by create and edit.")] string? description = null,
        [Description("public | private (create and edit).")] string? visibility = null,
        [Description("For create: local folder to publish; with push=true, pushes its commits.")] string? directory = null,
        [Description("For create: push the local commits as part of the creation.")] bool push = false,
        [Description("Repository as owner/name, for view/fork/clone/edit/delete.")] string? repo = null,
        [Description("Must be true for delete.")] bool confirm = false)
        => github.RepoAsync(action, name, description, visibility, confirm, repo, directory, push);
}
