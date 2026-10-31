using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitPatchTool
{
    [McpServerTool(Name = "git_patch"), Description(
        "Named shelves on disk: a patch you can list, inspect and apply later, across branches, worktrees and " +
        "machines (git_stash is the anonymous, in-repo variant). action=create writes the current diff to a file " +
        "(target working (default), staged, or any revision/range like 'main..HEAD'; path narrows it); action=list " +
        "shows the stored patches with their size and touched files; action=show prints one; action=apply runs " +
        "'git apply' (check=true only validates, reverse=true undoes the change, --3way is used when a plain apply " +
        "fails); action=delete removes one (confirm=true). Patches live in <git-dir>/visualmcp-patches unless " +
        "directory is given. Use this INSTEAD OF shell 'git diff > file.patch' + 'git apply'.")]
    public static Task<object> GitPatch(
        IGitService git,
        [Description("create | list | show | apply | delete")] string action = "list",
        [Description("The patch file name, e.g. 'wip-api.patch' (create: optional, defaults to a timestamp; show/apply/delete: required).")] string? name = null,
        [Description("For create: working (default), staged, or a revision/range (e.g. 'main..HEAD', 'HEAD~2').")] string? target = null,
        [Description("For create: only this file or folder.")] string? path = null,
        [Description("For apply: undo the patch instead of applying it.")] bool reverse = false,
        [Description("For apply: validate that the patch applies and change nothing.")] bool check = false,
        [Description("For delete: confirms the removal (required).")] bool confirm = false,
        [Description("Where the patches live. Defaults to <git-dir>/visualmcp-patches.")] string? directory = null,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.PatchAsync(action, name, target, path, reverse, check, confirm, directory, repoPath);
}
