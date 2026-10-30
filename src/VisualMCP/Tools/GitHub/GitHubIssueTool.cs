using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubIssueTool
{
    [McpServerTool(Name = "gh_issue"), Description(
        "Work with issues: list (parsed), view (body, labels, comments), create (title, body, comma-separated " +
        "labels), close/reopen (confirm=true) or comment. Use this INSTEAD OF shell 'gh issue ...'.")]
    public static Task<object> GitHubIssue(
        IGitHubService github,
        [Description("list | view | create | close | reopen | comment")] string action = "list",
        [Description("Issue number (view/close/reopen/comment).")] int? number = null,
        [Description("Title (create).")] string? title = null,
        [Description("Body for create, text for comment.")] string? body = null,
        [Description("Comma-separated labels, e.g. 'bug,backend'.")] string? labels = null,
        [Description("Must be true for close/reopen.")] bool confirm = false,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null)
        => github.IssueAsync(action, number, title, body, labels, confirm, repo);
}
