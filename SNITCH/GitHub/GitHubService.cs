using System.Text.Json;
using Snitch.Execution;
using Snitch.Workspace;

namespace Snitch.GitHub;

/// <summary>
/// GitHub operations through the authenticated <c>gh</c> CLI: repositories, pull requests, issues,
/// releases, Actions runs and secrets. Reads use <c>--json</c> and come back parsed; the commands
/// that change something visible to others (merge, close, delete, push a secret) require
/// <c>confirm=true</c>. <see cref="RunAsync"/> stays available as a raw escape hatch.
/// </summary>
public interface IGitHubService
{
    Task<object> RunAsync(string args, string? workingDirectory, int timeoutSeconds);
    Task<object> RepoAsync(string action, string? name, string? description, string? visibility, bool confirm, string? repo, string? directory, bool push);
    Task<object> PullRequestAsync(string action, int? number, string? title, string? body, string? baseBranch, string? headBranch, string? mergeMethod, bool deleteBranch, bool confirm, string? repo);
    Task<object> IssueAsync(string action, int? number, string? title, string? body, string? labels, bool confirm, string? repo);
    Task<object> ReleaseAsync(string action, string? tag, string? title, string? notes, string? asset, bool draft, bool prerelease, bool confirm, string? repo);
    Task<object> ActionsAsync(string action, string? runId, string? workflow, string? branch, bool failedOnly, bool confirm, string? repo);
    Task<object> SecretsAsync(string action, string? name, string? value, bool confirm, string? repo);
}

public sealed class GitHubService : IGitHubService
{
    private readonly IRoslynWorkspace _workspace;

    public GitHubService(IRoslynWorkspace workspace) => _workspace = workspace;

    // ── raw escape hatch ─────────────────────────────────────────────────────
    public async Task<object> RunAsync(string args, string? workingDirectory, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(args))
            return new { error = "Provide gh arguments, e.g. 'repo view' or 'pr list'." };

        return await ExecuteAsync(args, workingDirectory, timeoutSeconds, json: false);
    }

    // ── repositories ─────────────────────────────────────────────────────────
    public async Task<object> RepoAsync(string action, string? name, string? description, string? visibility,
        bool confirm, string? repo, string? directory, bool push)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();

        switch (verb)
        {
            case "list":
                return await ExecuteAsync("repo list --json nameWithOwner,description,isPrivate,visibility,updatedAt --limit 50",
                    null, 60, json: true);

            case "view":
                return await ExecuteAsync(
                    $"repo view {Target(repo)} --json name,nameWithOwner,description,isPrivate,visibility,defaultBranchRef,url,stargazerCount,forkCount",
                    null, 60, json: true);

            case "create":
            {
                if (string.IsNullOrWhiteSpace(name)) return new { error = "A repository name is required." };
                var args = $"repo create {name}";
                if (!string.IsNullOrWhiteSpace(description)) args += $" --description {Q(description!)}";
                args += string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase) ? " --public" : " --private";
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    args += $" --source {Q(directory!)}";
                    if (push) args += " --push";
                }

                return await ExecuteAsync(args, null, 180, json: false);
            }

            case "fork":
                if (string.IsNullOrWhiteSpace(repo)) return new { error = "The repository to fork is required (owner/name)." };
                return await ExecuteAsync($"repo fork {repo} --clone=false", null, 180, json: false);

            case "clone":
                if (string.IsNullOrWhiteSpace(repo)) return new { error = "The repository to clone is required (owner/name)." };
                return await ExecuteAsync($"repo clone {repo}{(string.IsNullOrWhiteSpace(directory) ? "" : $" {Q(directory!)}")}",
                    null, 300, json: false);

            case "edit":
            {
                if (string.IsNullOrWhiteSpace(repo)) return new { error = "The repository is required (owner/name)." };
                var args = $"repo edit {repo}";
                if (!string.IsNullOrWhiteSpace(description)) args += $" --description {Q(description!)}";
                if (!string.IsNullOrWhiteSpace(visibility)) args += $" --visibility {visibility!.ToLowerInvariant()} --accept-visibility-change-consequences";
                if (args == $"repo edit {repo}") return new { error = "Nothing to edit: pass description and/or visibility." };
                return await ExecuteAsync(args, null, 120, json: false);
            }

            case "delete":
                if (string.IsNullOrWhiteSpace(repo)) return new { error = "The repository is required (owner/name)." };
                if (!confirm) return new { error = "Deleting a repository is irreversible. Pass confirm=true to proceed.", repo };
                return await ExecuteAsync($"repo delete {repo} --yes", null, 120, json: false);

            default:
                return new { error = $"Unknown action '{action}'. Use list, view, create, fork, clone, edit or delete." };
        }
    }

    // ── pull requests ────────────────────────────────────────────────────────
    public async Task<object> PullRequestAsync(string action, int? number, string? title, string? body,
        string? baseBranch, string? headBranch, string? mergeMethod, bool deleteBranch, bool confirm, string? repo)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var target = Target(repo);

        switch (verb)
        {
            case "list":
                return await ExecuteAsync(
                    $"pr list{target} --json number,title,state,isDraft,author,headRefName,baseRefName,url,updatedAt --limit 40",
                    null, 60, json: true);

            case "view":
                if (number is null) return new { error = "A pull request number is required." };
                return await ExecuteAsync(
                    $"pr view {number}{target} --json number,title,body,state,isDraft,author,baseRefName,headRefName,mergeable,mergeStateStatus,url,comments,reviews",
                    null, 60, json: true);

            case "create":
            {
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                var args = $"pr create --title {Q(title!)}";
                args += string.IsNullOrWhiteSpace(body) ? " --body \"\"" : $" --body {Q(body!)}";
                if (!string.IsNullOrWhiteSpace(baseBranch)) args += $" --base {Q(baseBranch!)}";
                if (!string.IsNullOrWhiteSpace(headBranch)) args += $" --head {Q(headBranch!)}";
                return await ExecuteAsync(args + target, null, 120, json: false);
            }

            case "merge":
            {
                if (number is null) return new { error = "A pull request number is required." };
                if (!confirm) return new { error = "Merging is visible to others. Pass confirm=true to proceed.", number };
                var method = (mergeMethod ?? "merge").Trim().ToLowerInvariant() switch
                {
                    "merge" => "--merge",
                    "squash" => "--squash",
                    "rebase" => "--rebase",
                    _ => null,
                };
                if (method is null) return new { error = "mergeMethod must be merge, squash or rebase." };
                return await ExecuteAsync($"pr merge {number} {method}{(deleteBranch ? " --delete-branch" : string.Empty)}{target}",
                    null, 180, json: false);
            }

            case "close":
            case "reopen":
                if (number is null) return new { error = "A pull request number is required." };
                if (!confirm) return new { error = $"Pass confirm=true to {verb} pull request #{number}.", number };
                return await ExecuteAsync($"pr {verb} {number}{target}", null, 120, json: false);

            case "comment":
                if (number is null) return new { error = "A pull request number is required." };
                if (string.IsNullOrWhiteSpace(body)) return new { error = "A comment body is required." };
                return await ExecuteAsync($"pr comment {number} --body {Q(body!)}{target}", null, 120, json: false);

            case "diff":
                if (number is null) return new { error = "A pull request number is required." };
                return await ExecuteAsync($"pr diff {number}{target}", null, 120, json: false);

            case "checkout":
                if (number is null) return new { error = "A pull request number is required." };
                return await ExecuteAsync($"pr checkout {number}{target}", null, 180, json: false);

            case "ready":
                if (number is null) return new { error = "A pull request number is required." };
                return await ExecuteAsync($"pr ready {number}{target}", null, 120, json: false);

            default:
                return new { error = $"Unknown action '{action}'. Use list, view, create, merge, close, reopen, comment, diff, checkout or ready." };
        }
    }

    // ── issues ───────────────────────────────────────────────────────────────
    public async Task<object> IssueAsync(string action, int? number, string? title, string? body,
        string? labels, bool confirm, string? repo)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var target = Target(repo);

        switch (verb)
        {
            case "list":
                return await ExecuteAsync(
                    $"issue list{target} --json number,title,state,author,labels,url,updatedAt --limit 40",
                    null, 60, json: true);

            case "view":
                if (number is null) return new { error = "An issue number is required." };
                return await ExecuteAsync($"issue view {number}{target} --json number,title,body,state,author,labels,comments,url",
                    null, 60, json: true);

            case "create":
            {
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                var args = $"issue create --title {Q(title!)}";
                args += string.IsNullOrWhiteSpace(body) ? " --body \"\"" : $" --body {Q(body!)}";
                if (!string.IsNullOrWhiteSpace(labels)) args += $" --label {Q(labels!)}";
                return await ExecuteAsync(args + target, null, 120, json: false);
            }

            case "close":
            case "reopen":
                if (number is null) return new { error = "An issue number is required." };
                if (!confirm) return new { error = $"Pass confirm=true to {verb} issue #{number}.", number };
                return await ExecuteAsync($"issue {verb} {number}{target}", null, 120, json: false);

            case "comment":
                if (number is null) return new { error = "An issue number is required." };
                if (string.IsNullOrWhiteSpace(body)) return new { error = "A comment body is required." };
                return await ExecuteAsync($"issue comment {number} --body {Q(body!)}{target}", null, 120, json: false);

            default:
                return new { error = $"Unknown action '{action}'. Use list, view, create, close, reopen or comment." };
        }
    }

    // ── releases ─────────────────────────────────────────────────────────────
    public async Task<object> ReleaseAsync(string action, string? tag, string? title, string? notes,
        string? asset, bool draft, bool prerelease, bool confirm, string? repo)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var target = Target(repo);

        switch (verb)
        {
            case "list":
                return await ExecuteAsync($"release list{target} --json tagName,name,isDraft,isPrerelease,publishedAt --limit 30",
                    null, 60, json: true);

            case "view":
                if (string.IsNullOrWhiteSpace(tag)) return new { error = "A tag is required." };
                return await ExecuteAsync($"release view {tag}{target} --json tagName,name,body,isDraft,isPrerelease,publishedAt,assets,url",
                    null, 60, json: true);

            case "create":
            {
                if (string.IsNullOrWhiteSpace(tag)) return new { error = "A tag is required (e.g. v1.2.0)." };
                var args = $"release create {tag}";
                if (!string.IsNullOrWhiteSpace(title)) args += $" --title {Q(title!)}";
                args += string.IsNullOrWhiteSpace(notes) ? " --generate-notes" : $" --notes {Q(notes!)}";
                if (draft) args += " --draft";
                if (prerelease) args += " --prerelease";
                if (!string.IsNullOrWhiteSpace(asset)) args += $" {Q(asset!)}";
                return await ExecuteAsync(args + target, null, 300, json: false);
            }

            case "upload":
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(asset))
                    return new { error = "Both a tag and one or more asset paths are required." };
                return await ExecuteAsync($"release upload {tag} {asset}{target}", null, 600, json: false);

            case "delete":
                if (string.IsNullOrWhiteSpace(tag)) return new { error = "A tag is required." };
                if (!confirm) return new { error = "Deleting a release is irreversible. Pass confirm=true to proceed.", tag };
                return await ExecuteAsync($"release delete {tag} --yes{target}", null, 120, json: false);

            default:
                return new { error = $"Unknown action '{action}'. Use list, view, create, upload or delete." };
        }
    }

    // ── actions ──────────────────────────────────────────────────────────────
    public async Task<object> ActionsAsync(string action, string? runId, string? workflow, string? branch,
        bool failedOnly, bool confirm, string? repo)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var target = Target(repo);

        switch (verb)
        {
            case "list":
            {
                var args = $"run list{target} --json databaseId,name,displayTitle,status,conclusion,headBranch,event,createdAt --limit 20";
                if (!string.IsNullOrWhiteSpace(workflow)) args += $" --workflow {Q(workflow!)}";
                if (!string.IsNullOrWhiteSpace(branch)) args += $" --branch {Q(branch!)}";
                return await ExecuteAsync(args, null, 90, json: true);
            }

            case "view":
                if (string.IsNullOrWhiteSpace(runId)) return new { error = "A run id is required (see action=list)." };
                return await ExecuteAsync($"run view {runId}{target} --json databaseId,name,status,conclusion,headBranch,event,jobs,url",
                    null, 90, json: true);

            case "logs":
                if (string.IsNullOrWhiteSpace(runId)) return new { error = "A run id is required (see action=list)." };
                return await ExecuteAsync($"run view {runId}{target} --log", null, 180, json: false);

            case "rerun":
                if (string.IsNullOrWhiteSpace(runId)) return new { error = "A run id is required (see action=list)." };
                if (!confirm) return new { error = "Rerunning a workflow is visible to others. Pass confirm=true to proceed.", runId };
                return await ExecuteAsync($"run rerun {runId}{(failedOnly ? " --failed" : string.Empty)}{target}", null, 120, json: false);

            case "cancel":
                if (string.IsNullOrWhiteSpace(runId)) return new { error = "A run id is required (see action=list)." };
                if (!confirm) return new { error = "Cancelling a run is visible to others. Pass confirm=true to proceed.", runId };
                return await ExecuteAsync($"run cancel {runId}{target}", null, 120, json: false);

            case "workflows":
                return await ExecuteAsync($"workflow list{target} --json id,name,path,state --limit 60", null, 90, json: true);

            case "dispatch":
            {
                if (string.IsNullOrWhiteSpace(workflow)) return new { error = "A workflow (name or file) is required." };
                if (!confirm) return new { error = "Dispatching a workflow is visible to others. Pass confirm=true to proceed.", workflow };
                var args = $"workflow run {Q(workflow!)}";
                if (!string.IsNullOrWhiteSpace(branch)) args += $" --ref {Q(branch!)}";
                return await ExecuteAsync(args + target, null, 120, json: false);
            }

            default:
                return new { error = $"Unknown action '{action}'. Use list, view, logs, rerun, cancel, workflows or dispatch." };
        }
    }

    // ── secrets ──────────────────────────────────────────────────────────────
    public async Task<object> SecretsAsync(string action, string? name, string? value, bool confirm, string? repo)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var target = Target(repo);

        switch (verb)
        {
            case "list":
                return await ExecuteAsync($"secret list{target} --json name,updatedAt", null, 60, json: true);

            case "set":
                if (string.IsNullOrWhiteSpace(name)) return new { error = "A secret name is required." };
                if (string.IsNullOrWhiteSpace(value)) return new { error = "A secret value is required." };
                if (!confirm) return new { error = "Writing a repository secret changes remote configuration. Pass confirm=true to proceed.", name };
                // The value travels on the command line and is never echoed in the result.
                return await ExecuteAsync($"secret set {name} --body {Q(value!)}{target}", null, 120, json: false);

            case "delete":
                if (string.IsNullOrWhiteSpace(name)) return new { error = "A secret name is required." };
                if (!confirm) return new { error = "Deleting a secret is irreversible. Pass confirm=true to proceed.", name };
                return await ExecuteAsync($"secret delete {name}{target}", null, 120, json: false);

            default:
                return new { error = $"Unknown action '{action}'. Use list, set or delete." };
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────
    private async Task<object> ExecuteAsync(string args, string? workingDirectory, int timeoutSeconds, bool json)
    {
        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 900) timeoutSeconds = 900;

        string dir;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (!Directory.Exists(workingDirectory)) return new { error = $"Working directory not found: {workingDirectory}" };
            dir = Path.GetFullPath(workingDirectory);
        }
        else
        {
            var solutionPath = _workspace.LoadedSolutionPath;
            dir = solutionPath is not null ? Path.GetDirectoryName(solutionPath)! : Directory.GetCurrentDirectory();
        }

        var (exitCode, timedOut, stdout, stderr, elapsed) = await ProcessRunner.RunAsync("gh", args, dir, timeoutSeconds);

        if (timedOut) return new { error = $"gh timed out after {timeoutSeconds}s.", command = $"gh {args}" };

        object? data = null;
        if (json && exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var document = JsonDocument.Parse(stdout);
                data = document.RootElement.Clone();
            }
            catch (JsonException) { /* fall back to the raw text */ }
        }

        return new
        {
            command = $"gh {args}",
            workingDirectory = dir,
            exitCode,
            succeeded = exitCode == 0,
            elapsedMs = (int)elapsed.TotalMilliseconds,
            data,
            stdout = ProcessRunner.Truncate(stdout, 24_000),
            stderr = ProcessRunner.Truncate(stderr, 8000),
            note = exitCode != 0 && (stderr + stdout).Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? "If gh is not installed or not authenticated, run 'gh auth login' once in a terminal."
                : null,
        };
    }

    /// <summary>`--repo owner/name` when the caller named a repository; empty otherwise.</summary>
    private static string Target(string? repo) =>
        string.IsNullOrWhiteSpace(repo) ? string.Empty : $" --repo {repo}";

    private static string Q(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
