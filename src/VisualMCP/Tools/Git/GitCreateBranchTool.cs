using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitCreateBranchTool
{
    [McpServerTool(Name = "git_create_branch"), Description(
        "Create a new git branch in a repository and (by default) switch to it. Optionally branch from a " +
        "specific start point. Targets the loaded solution's repo unless repoPath is given. Does NOT push. " +
        "Fails if the branch already exists.")]
    public static Task<object> GitCreateBranch(
        [Description("Name of the new branch.")] string name,
        [Description("Switch to the new branch after creating it (default: true).")] bool checkout = true,
        [Description("Optional: start point (branch, tag or commit) to create the branch from. Defaults to the current HEAD.")] string? startPoint = null,
        [Description("Optional: path to the git repository. Defaults to the loaded solution's repo.")] string? repoPath = null)
        => GitImpl.CreateBranchAsync(name, checkout, startPoint, repoPath);
}
