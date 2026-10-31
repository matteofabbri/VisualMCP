using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Git;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitConflictsTool
{
    [McpServerTool(Name = "git_conflicts"), Description(
        "The merge assistant: a merge editor as data. action=list reports what the repository is in the middle of " +
        "(merge, rebase, cherry-pick, revert) and every conflicted file with its conflict hunks (ours/theirs/base " +
        "line counts, labels, first lines); action=show returns each hunk of one file with all three sides and the " +
        "diff3 text; action=resolve rewrites the markers away with strategy ours, theirs, both (ours then theirs), " +
        "base (needs merge.conflictStyle=diff3) or custom (with content), for one hunk (blockIndex) or the whole " +
        "file, and stages the file when no markers remain (stage=false to keep it unstaged). After a merge, commit; " +
        "after a rebase/cherry-pick, git_rebase/git_cherry_pick action=continue. Use this INSTEAD OF editing " +
        "'<<<<<<<' blocks by hand or shell 'git checkout --ours/--theirs'.")]
    public static Task<object> GitConflicts(
        IGitService git,
        [Description("list | show | resolve")] string action = "list",
        [Description("The conflicted file, relative to the repository (required for show and resolve).")] string? path = null,
        [Description("For resolve: ours | theirs | both | base | custom")] string strategy = "ours",
        [Description("For resolve: which hunk to rewrite (0-based, as action=list/show report it). Omit for every hunk.")] int? blockIndex = null,
        [Description("For resolve with strategy=custom: the text that replaces the hunk (its trailing newline is optional).")] string? content = null,
        [Description("For resolve: stage the file once no markers remain (default true).")] bool stage = true,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null)
        => git.ConflictsAsync(action, path, strategy, blockIndex, content, stage, repoPath);
}
