using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubBranchProtectionTool
{
    [McpServerTool(Name = "gh_branch_protection"), Description(
        "Inspect branch protection on GitHub, read-only: action=get returns whether the branch is protected and, " +
        "when the token has admin access, the required status checks, required pull-request reviews, the " +
        "enforce-admins flag and any push restrictions; action=checks narrows that to the required status checks " +
        "(strict mode and the contexts/checks a PR must satisfy); action=rules returns the repository RULESETS " +
        "that apply to the branch, which the public API exposes without admin access. branch defaults to the " +
        "repository's default branch; repo is owner/name for another repository. Use this INSTEAD OF shell " +
        "'gh api repos/{owner}/{repo}/branches/…/protection'.")]
    public static Task<object> GitHubBranchProtection(
        IGitHubService github,
        [Description("get | checks | rules")] string action = "get",
        [Description("The branch to inspect (default: the repository's default branch).")] string? branch = null,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null)
        => github.BranchProtectionAsync(action, branch, repo);
}
