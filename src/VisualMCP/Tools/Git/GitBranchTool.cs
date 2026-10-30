using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitBranchTool
{
    [McpServerTool(Name = "git_branch"), Description(
        "Manage branches: action=list (default) shows every branch with its upstream, last commit date and subject; " +
        "create makes one (optionally checked out, from a start point); delete removes it (force=true for unmerged); " +
        "rename renames one (`from` defaults to the current branch); switch checks one out (create=true makes a new " +
        "branch and switches to it). Use this INSTEAD OF shell 'git branch' / 'git switch'.")]
    public static Task<object> GitBranch(
        IGitService git,
        [Description("list | create | delete | rename | switch")] string action = "list",
        [Description("Branch name (create/delete/rename target; switch destination).")] string? name = null,
        [Description("For rename: the branch to rename. Omit to rename the current branch.")] string? from = null,
        [Description("For create: the ref to start from (default HEAD).")] string? startPoint = null,
        [Description("For create/switch: also check the branch out.")] bool checkout = false,
        [Description("For delete: delete even if it holds unmerged commits.")] bool force = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => action.Trim().ToLowerInvariant() switch
        {
            "list" => git.BranchesAsync(repoPath),
            "create" => git.CreateBranchAsync(name ?? string.Empty, checkout, startPoint, repoPath),
            "delete" => git.DeleteBranchAsync(name ?? string.Empty, force, repoPath),
            "rename" => git.RenameBranchAsync(from ?? string.Empty, name ?? string.Empty, repoPath),
            "switch" => git.CheckoutAsync(name ?? string.Empty, create: false, repoPath),
            _ => Task.FromResult<object>(new { error = $"Unknown action '{action}'. Use list, create, delete, rename or switch." }),
        };
}
