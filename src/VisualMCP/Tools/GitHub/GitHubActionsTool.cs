using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.GitHub;

namespace VisualMCP.Tools.GitHub;

[McpServerToolType]
public static class GitHubActionsTool
{
    [McpServerTool(Name = "gh_actions"), Description(
        "Inspect and drive GitHub Actions: list recent runs (parsed, filterable by workflow/branch), view one run " +
        "with its jobs, download its logs, rerun (only the failed jobs if failedOnly), cancel, list workflows, or " +
        "dispatch one. rerun/cancel/dispatch require confirm=true. Use this INSTEAD OF shell 'gh run ...'.")]
    public static Task<object> GitHubActions(
        IGitHubService github,
        [Description("list | view | logs | rerun | cancel | workflows | dispatch")] string action = "list",
        [Description("Run id (view/logs/rerun/cancel).")] string? runId = null,
        [Description("Workflow name or file, e.g. 'build.yml' (list filter, dispatch).")] string? workflow = null,
        [Description("Branch filter for list, or the ref to dispatch on.")] string? branch = null,
        [Description("For rerun: only the failed jobs.")] bool failedOnly = false,
        [Description("Must be true for rerun/cancel/dispatch.")] bool confirm = false,
        [Description("Repository as owner/name (default: the current one).")] string? repo = null)
        => github.ActionsAsync(action, runId, workflow, branch, failedOnly, confirm, repo);
}
