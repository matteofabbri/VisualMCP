using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubPullRequestTool
{
    [McpServerTool(Name = "gh_pr"), Description(
        "Work with pull requests: list (parsed), view (body, reviews, comments, mergeability), create (from the " +
        "current branch, optionally base/head), merge (method merge|squash|rebase, confirm=true required), close, " +
        "reopen, comment, diff, checkout locally, or mark ready. Use this INSTEAD OF shell 'gh pr ...'.")]
    public static Task<object> GitHubPullRequest(
        IGitHubService github,
        [Description("list | view | create | merge | close | reopen | comment | diff | checkout | ready")] string action = "list",
        [Description("Pull request number (view/merge/close/reopen/comment/diff/checkout/ready).")] int? number = null,
        [Description("Title (create).")] string? title = null,
        [Description("Body: description for create, text for comment.")] string? body = null,
        [Description("Base branch to merge into (create).")] string? baseBranch = null,
        [Description("Head branch with the changes (create).")] string? headBranch = null,
        [Description("For merge: merge | squash | rebase.")] string? mergeMethod = null,
        [Description("For merge: delete the head branch after merging.")] bool deleteBranch = false,
        [Description("Must be true for merge/close/reopen.")] bool confirm = false,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null)
        => github.PullRequestAsync(action, number, title, body, baseBranch, headBranch, mergeMethod, deleteBranch, confirm, repo);
}
