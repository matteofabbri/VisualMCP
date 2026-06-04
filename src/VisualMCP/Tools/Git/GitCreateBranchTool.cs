using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitCreateBranchTool
{
    [McpServerTool(Name = "git_create_branch"), Description(
        "Create a new git branch in the loaded solution's repository and (by default) switch to it. " +
        "Optionally branch from a specific start point (ref/commit). Does NOT push. Fails if the branch " +
        "already exists.")]
    public static Task<object> GitCreateBranch(
        [Description("Name of the new branch.")] string name,
        [Description("Switch to the new branch after creating it (default: true). If false, the branch is created but you stay on the current one.")] bool checkout = true,
        [Description("Optional: start point (branch, tag or commit) to create the branch from. Defaults to the current HEAD.")] string? startPoint = null)
        => GitImpl.CreateBranchAsync(name, checkout, startPoint);
}
