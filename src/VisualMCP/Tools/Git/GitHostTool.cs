using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Collaboration;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitHostTool
{
    [McpServerTool(Name = "git_host"), Description(
        "The hosting services other than GitHub — GitLab, Azure DevOps and Bitbucket Cloud — as one tool, the " +
        "counterpart of the gh_* tools (GitHub keeps gh_pr/gh_issue/gh_actions, which also cover line-anchored " +
        "reviews and check runs). The provider, host, project and repository are read from the repository's " +
        "remote URL (https, ssh:// and scp-like forms) and can be overridden by the arguments; action=detect " +
        "reports what was resolved and whether a credential is present in the environment (GITLAB_TOKEN, " +
        "AZURE_DEVOPS_EXT_PAT/AZURE_DEVOPS_TOKEN, BITBUCKET_TOKEN or BITBUCKET_USERNAME + BITBUCKET_APP_PASSWORD). " +
        "Actions: detect, list-prs (state open|closed|merged|all, limit), view-pr (number), create-pr " +
        "(sourceBranch, targetBranch, title, body; confirm=true), comment-pr (number, body; confirm=true), merge-pr " +
        "(number; confirm=true), review-pr (number, reviewEvent approve|request-changes|comment, body; confirm=true — " +
        "GitLab has no request-changes vote and says so), close-pr / reopen-pr (number; confirm=true), update-pr " +
        "(number, title and/or body; confirm=true), list-issues, create-issue (title, body; confirm=true), pipelines " +
        "(recent pipelines/builds). For Bitbucket Cloud the project is the workspace; for GitLab it is the full " +
        "group/project path; for Azure DevOps pass hostUrl=https://dev.azure.com/{organization} when the remote " +
        "does not carry the organization. Use this INSTEAD OF curl/Invoke-RestMethod against those APIs.")]
    public static Task<object> GitHost(
        IRemoteHostService host,
        [Description("detect | list-prs | view-pr | create-pr | comment-pr | merge-pr | review-pr | close-pr | reopen-pr | update-pr | list-issues | create-issue | pipelines")] string action = "detect",
        [Description("gitlab | azuredevops | bitbucket | auto (default: inferred from the remote URL).")] string provider = "auto",
        [Description("API base URL for a self-hosted instance (e.g. https://gitlab.example.com or https://dev.azure.com/{organization}).")] string? hostUrl = null,
        [Description("GitLab group/project path, Azure DevOps project, or Bitbucket workspace. Defaults to what the remote URL says.")] string? project = null,
        [Description("The repository name. Defaults to what the remote URL says.")] string? repository = null,
        [Description("The pull/merge request number, or the issue/work-item number.")] int? number = null,
        [Description("Title for create-pr / create-issue, or the new title for update-pr.")] string? title = null,
        [Description("Body/description for create-pr, create-issue, update-pr or a review comment, or the text of comment-pr.")] string? body = null,
        [Description("For create-pr: the branch with the change.")] string? sourceBranch = null,
        [Description("For create-pr: the branch it targets.")] string? targetBranch = null,
        [Description("For list-prs: open (default), closed, merged, all. For list-issues: open (default) or all.")] string? state = null,
        [Description("How many items list actions return (default 20, max 100).")] int limit = 20,
        [Description("Required for create-pr, comment-pr, merge-pr, review-pr, close-pr, reopen-pr, update-pr, create-issue — they are visible to others.")] bool confirm = false,
        [Description("Repository path. Defaults to the loaded solution's repository.")] string? repoPath = null,
        [Description("Request timeout in seconds (default 60).")] int timeoutSeconds = 60,
        [Description("For review-pr: approve | request-changes | comment (default comment, which needs a body).")] string? reviewEvent = null)
        => host.HostAsync(action, provider, hostUrl, project, repository, number, title, body,
            sourceBranch, targetBranch, state, limit, confirm, repoPath, timeoutSeconds, reviewEvent);
}
