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
        "reopen, comment, review (a REVIEW with line-anchored comments: pass comments as JSON " +
        "'[{\"path\":\"src/A.cs\",\"line\":42,\"side\":\"RIGHT\",\"body\":\"…\"}]' and reviewEvent " +
        "comment|approve|request-changes; the line is a line of the diff, side RIGHT = the new file), review-comments " +
        "(every inline comment on the PR), checks (the check runs on the PR head, with state and link), diff, " +
        "checkout locally, or mark ready. Use this INSTEAD OF shell 'gh pr ...'.")]
    public static Task<object> GitHubPullRequest(
        IGitHubService github,
        [Description("list | view | create | merge | close | reopen | comment | review | review-comments | checks | diff | checkout | ready")] string action = "list",
        [Description("Pull request number (view/merge/close/reopen/comment/review/review-comments/checks/diff/checkout/ready).")] int? number = null,
        [Description("Title (create).")] string? title = null,
        [Description("Body: description for create, text for comment, the review summary for review.")] string? body = null,
        [Description("Base branch to merge into (create).")] string? baseBranch = null,
        [Description("Head branch with the changes (create).")] string? headBranch = null,
        [Description("For merge: merge | squash | rebase.")] string? mergeMethod = null,
        [Description("For merge: delete the head branch after merging.")] bool deleteBranch = false,
        [Description("Must be true for merge/close/reopen/review.")] bool confirm = false,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null,
        [Description("For review: JSON array of line comments, e.g. [{\"path\":\"src/A.cs\",\"line\":42,\"side\":\"RIGHT\",\"body\":\"…\"}].")] string? comments = null,
        [Description("For review: comment (default) | approve | request-changes.")] string? reviewEvent = null)
        => github.PullRequestAsync(action, number, title, body, baseBranch, headBranch, mergeMethod, deleteBranch,
            confirm, repo, comments, reviewEvent);
}
