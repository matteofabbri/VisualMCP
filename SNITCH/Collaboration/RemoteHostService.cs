using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Snitch.Git;

namespace Snitch.Collaboration;

/// <summary>Where a repository lives on a hosting service, as parsed from its remote URL.</summary>
public sealed record HostCoordinates(
    string Provider,
    string Host,
    string BaseUrl,
    string Project,
    string Repository)
{
    /// <summary>The provider's own name for the workspace/project field.</summary>
    public string ProjectKind => Provider switch
    {
        RemoteHostService.GitLab => "group/project path",
        RemoteHostService.AzureDevOps => "project",
        _ => "workspace",
    };
}

/// <summary>
/// The hosting providers other than GitHub: merge/pull requests, their comments, merges, reviews and
/// the close/reopen/update lifecycle, plus issues and pipelines/builds, over each vendor's HTTP API.
/// GitHub stays on the <c>gh</c> CLI (gh_*), which also covers line-anchored reviews and checks; this
/// is the equivalent for GitLab, Azure DevOps and Bitbucket.
/// </summary>
public interface IRemoteHostService
{
    Task<object> HostAsync(
        string action,
        string provider,
        string? hostUrl,
        string? project,
        string? repository,
        int? number,
        string? title,
        string? body,
        string? sourceBranch,
        string? targetBranch,
        string? state,
        int? limit,
        bool confirm,
        string? repoPath,
        int timeoutSeconds,
        string? reviewEvent = null);
}

/// <summary>Default implementation over HttpClient, with per-provider request and response mapping.</summary>
public sealed class RemoteHostService : IRemoteHostService
{
    public const string GitLab = "gitlab";
    public const string AzureDevOps = "azuredevops";
    public const string Bitbucket = "bitbucket";
    public const string GitHub = "github";

    private static readonly string[] Providers = [GitLab, AzureDevOps, Bitbucket];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Snitch.Workspace.IRoslynWorkspace? _workspace;
    private readonly HttpClient _http;

    public RemoteHostService(Snitch.Workspace.IRoslynWorkspace workspace, HttpMessageHandler? handler = null)
    {
        _workspace = workspace;
        _http = handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A test-only service that talks to the injected handler instead of the network.</summary>
    public RemoteHostService(HttpMessageHandler handler) : this(null!, handler) { }

    /// <summary>
    /// The API base when no remote names it: Bitbucket Cloud has one fixed API host, while GitLab and
    /// Azure DevOps must be told (a self-hosted instance has no default).
    /// </summary>
    private static string? DefaultHost(string provider) => provider == Bitbucket ? "https://api.bitbucket.org" : null;

    public async Task<object> HostAsync(
        string action, string provider, string? hostUrl, string? project, string? repository,
        int? number, string? title, string? body, string? sourceBranch, string? targetBranch,
        string? state, int? limit, bool confirm, string? repoPath, int timeoutSeconds,
        string? reviewEvent = null)
    {
        var verb = (action ?? "detect").Trim().ToLowerInvariant();
        var site = await ResolveAsync(provider, hostUrl, project, repository, repoPath);
        if (site.Error is not null) return site.Error;
        var at = site.Coordinates!;

        if (verb == "detect")
            return new
            {
                provider = at.Provider,
                host = at.Host,
                baseUrl = at.BaseUrl,
                project = at.Project,
                repository = at.Repository,
                projectKind = at.ProjectKind,
                remoteUrl = site.RemoteUrl,
                auth = AuthStatus(at.Provider),
                supportedActions = new[]
                {
                    "detect", "list-prs", "view-pr", "create-pr", "comment-pr", "merge-pr",
                    "review-pr", "close-pr", "reopen-pr", "update-pr",
                    "list-issues", "create-issue", "pipelines",
                },
                note = "GitHub repositories use gh_* (gh_pr, gh_issue, gh_actions), which also covers line-anchored reviews and checks.",
            };

        var credential = Credential(at.Provider);
        if (credential is null) return MissingCredential(at.Provider);

        var clamped = Math.Clamp(limit ?? 20, 1, 100);
        var prState = string.IsNullOrWhiteSpace(state) ? "open" : state!.Trim().ToLowerInvariant();
        var token = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 300))).Token;

        try
        {
            return at.Provider switch
            {
                GitLab => await GitLabAsync(verb, at, credential, number, title, body, sourceBranch, targetBranch, prState, clamped, confirm, token, reviewEvent),
                AzureDevOps => await AzureAsync(verb, at, credential, number, title, body, sourceBranch, targetBranch, prState, clamped, confirm, token, reviewEvent),
                Bitbucket => await BitbucketAsync(verb, at, credential, number, title, body, sourceBranch, targetBranch, prState, clamped, confirm, token, reviewEvent),
                _ => UnsupportedProvider(provider),
            };
        }
        catch (OperationCanceledException)
        {
            return new { error = $"The {at.Provider} request timed out after {timeoutSeconds}s.", url = at.BaseUrl };
        }
        catch (HttpRequestException ex)
        {
            return new { error = $"Could not reach {at.Host}: {ex.Message}", provider = at.Provider, host = at.Host };
        }
    }

    // ── GitLab ───────────────────────────────────────────────────────────────
    private async Task<object> GitLabAsync(string verb, HostCoordinates at, HostCredential credential,
        int? number, string? title, string? body, string? sourceBranch, string? targetBranch,
        string state, int limit, bool confirm, CancellationToken token, string? reviewEvent)
    {
        var project = Uri.EscapeDataString(at.Project);
        var api = $"{at.BaseUrl}/api/v4/projects/{project}";
        var headers = new Dictionary<string, string> { ["PRIVATE-TOKEN"] = credential.Value };

        switch (verb)
        {
            case "list-prs":
            {
                var wanted = state switch { "open" or "opened" => "opened", "closed" => "closed", "merged" => "merged", "all" => "all", _ => state };
                var result = await SendAsync(HttpMethod.Get, $"{api}/merge_requests?state={Uri.EscapeDataString(wanted)}&per_page={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, host = at.Host, project = at.Project, state = wanted, url = result.Url,
                    count = result.Items.Count,
                    items = result.Items.Select(mr => new
                    {
                        number = Int(mr, "iid"),
                        title = Text(mr, "title"),
                        state = Text(mr, "state"),
                        draft = Bool(mr, "draft"),
                        sourceBranch = Text(mr, "source_branch"),
                        targetBranch = Text(mr, "target_branch"),
                        author = Text(mr, "author", "name"),
                        updatedAt = Text(mr, "updated_at"),
                        url = Text(mr, "web_url"),
                    }).ToList(),
                };
            }

            case "view-pr":
                if (number is null) return NeedsNumber();
                {
                    var result = await SendAsync(HttpMethod.Get, $"{api}/merge_requests/{number}", headers, null, token);
                    return result.Ok
                        ? new
                        {
                            provider = at.Provider, url = result.Url, statusCode = result.StatusCode,
                            number = Int(result.Data, "iid"), title = Text(result.Data, "title"),
                            state = Text(result.Data, "state"), draft = Bool(result.Data, "draft"),
                            description = Text(result.Data, "description"),
                            sourceBranch = Text(result.Data, "source_branch"),
                            targetBranch = Text(result.Data, "target_branch"),
                            author = Text(result.Data, "author", "name"),
                            mergeStatus = Text(result.Data, "detailed_merge_status"),
                            hasConflicts = Bool(result.Data, "has_conflicts"),
                            sha = Text(result.Data, "sha"),
                            webUrl = Text(result.Data, "web_url"),
                            data = result.Data,
                        }
                        : Describe(result, at, verb);
                }

            case "create-pr":
                if (!confirm) return NeedsConfirm("Creating a merge request is visible to others.");
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
                    return new { error = "sourceBranch and targetBranch are required." };
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        source_branch = sourceBranch,
                        target_branch = targetBranch,
                        title,
                        description = body ?? string.Empty,
                    }, Json);

                    var result = await SendAsync(HttpMethod.Post, $"{api}/merge_requests", headers, payload, token);
                    return result.Ok
                        ? new
                        {
                            provider = at.Provider, created = true, url = result.Url, statusCode = result.StatusCode,
                            number = Int(result.Data, "iid"), title = Text(result.Data, "title"),
                            webUrl = Text(result.Data, "web_url"),
                        }
                        : Describe(result, at, verb);
                }

            case "comment-pr":
                if (!confirm) return NeedsConfirm("Commenting on a merge request is visible to others.");
                if (number is null) return NeedsNumber();
                if (string.IsNullOrWhiteSpace(body)) return new { error = "A comment body is required." };
                {
                    var payload = JsonSerializer.Serialize(new { body }, Json);
                    var result = await SendAsync(HttpMethod.Post, $"{api}/merge_requests/{number}/notes", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, commented = true, number, url = result.Url, statusCode = result.StatusCode, id = Int(result.Data, "id") }
                        : Describe(result, at, verb);
                }

            case "merge-pr":
                if (!confirm) return NeedsConfirm("Merging is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var result = await SendAsync(HttpMethod.Put, $"{api}/merge_requests/{number}/merge", headers, null, token);
                    return result.Ok
                        ? new { provider = at.Provider, merged = true, number, url = result.Url, statusCode = result.StatusCode, state = Text(result.Data, "state") }
                        : Describe(result, at, verb);
                }

            case "review-pr":
                if (!confirm) return NeedsConfirm("Posting a review is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var review = ReviewEvent(reviewEvent);
                    if (review is null) return BadReviewEvent();

                    if (review == "request-changes")
                        return new
                        {
                            provider = at.Provider, reviewed = false, supported = false, number,
                            reviewEvent = review,
                            error = "GitLab's REST API has no request-changes vote (only approve and unapprove); the requested-changes state exists in the UI, not in the API.",
                            hint = "Post the reason with comment-pr, then approve with review-pr reviewEvent=approve when it is resolved.",
                        };

                    if (review == "comment")
                    {
                        if (string.IsNullOrWhiteSpace(body)) return NeedsReviewBody();
                        var payload = JsonSerializer.Serialize(new { body }, Json);
                        var result = await SendAsync(HttpMethod.Post, $"{api}/merge_requests/{number}/notes", headers, payload, token);
                        return result.Ok
                            ? new { provider = at.Provider, reviewed = true, reviewEvent = review, number, id = Int(result.Data, "id"), url = result.Url, statusCode = result.StatusCode }
                            : Describe(result, at, verb);
                    }

                    var approved = await SendAsync(HttpMethod.Post, $"{api}/merge_requests/{number}/approve", headers, "{}", token);
                    return approved.Ok
                        ? new { provider = at.Provider, reviewed = true, reviewEvent = review, number, approved = true, url = approved.Url, statusCode = approved.StatusCode }
                        : Describe(approved, at, verb);
                }

            case "close-pr":
            case "reopen-pr":
                if (!confirm) return NeedsConfirm(verb == "close-pr"
                    ? "Closing a merge request is visible to others."
                    : "Reopening a merge request is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var payload = JsonSerializer.Serialize(new { state_event = verb == "close-pr" ? "close" : "reopen" }, Json);
                    var result = await SendAsync(HttpMethod.Put, $"{api}/merge_requests/{number}", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, number, action = verb, state = Text(result.Data, "state"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "update-pr":
                if (!confirm) return NeedsConfirm("Editing a merge request is visible to others.");
                if (number is null) return NeedsNumber();
                if (string.IsNullOrWhiteSpace(title) && body is null)
                    return new { error = "Pass title, body (the description), or both." };
                {
                    var fields = new Dictionary<string, object>();
                    if (!string.IsNullOrWhiteSpace(title)) fields["title"] = title!;
                    if (body is not null) fields["description"] = body;

                    var payload = JsonSerializer.Serialize(fields, Json);
                    var result = await SendAsync(HttpMethod.Put, $"{api}/merge_requests/{number}", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, updated = true, number, title = Text(result.Data, "title"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "list-issues":
            {
                var result = await SendAsync(HttpMethod.Get, $"{api}/issues?state={(state == "open" ? "opened" : Uri.EscapeDataString(state))}&per_page={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(issue => new
                    {
                        number = Int(issue, "iid"),
                        title = Text(issue, "title"),
                        state = Text(issue, "state"),
                        author = Text(issue, "author", "name"),
                        labels = Strings(issue, "labels"),
                        updatedAt = Text(issue, "updated_at"),
                        url = Text(issue, "web_url"),
                    }).ToList(),
                };
            }

            case "create-issue":
                if (!confirm) return NeedsConfirm("Creating an issue is visible to others.");
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                {
                    var payload = JsonSerializer.Serialize(new { title, description = body ?? string.Empty }, Json);
                    var result = await SendAsync(HttpMethod.Post, $"{api}/issues", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, created = true, number = Int(result.Data, "iid"), webUrl = Text(result.Data, "web_url"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "pipelines":
            {
                var result = await SendAsync(HttpMethod.Get, $"{api}/pipelines?per_page={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(p => new
                    {
                        id = Int(p, "id"),
                        status = Text(p, "status"),
                        reference = Text(p, "ref"),
                        source = Text(p, "source"),
                        updatedAt = Text(p, "updated_at"),
                        webUrl = Text(p, "web_url"),
                    }).ToList(),
                };
            }

            default:
                return UnknownAction(at.Provider);
        }
    }

    // ── Azure DevOps ─────────────────────────────────────────────────────────
    private async Task<object> AzureAsync(string verb, HostCoordinates at, HostCredential credential,
        int? number, string? title, string? body, string? sourceBranch, string? targetBranch,
        string state, int limit, bool confirm, CancellationToken token, string? reviewEvent)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = credential.Value };
        var repo = $"{at.BaseUrl}/{Uri.EscapeDataString(at.Project)}/_apis/git/repositories/{Uri.EscapeDataString(at.Repository)}";

        switch (verb)
        {
            case "list-prs":
            {
                var status = state switch { "open" or "active" => "active", "closed" or "completed" => "completed", "abandoned" => "abandoned", "all" => "all", _ => state };
                var result = await SendAsync(HttpMethod.Get,
                    $"{repo}/pullrequests?api-version=7.1&searchCriteria.status={Uri.EscapeDataString(status)}&$top={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, host = at.Host, project = at.Project, repository = at.Repository,
                    state = status, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(pr => new
                    {
                        number = Int(pr, "pullRequestId"),
                        title = Text(pr, "title"),
                        state = Text(pr, "status"),
                        draft = Bool(pr, "isDraft"),
                        sourceBranch = TrimRefs(Text(pr, "sourceRefName")),
                        targetBranch = TrimRefs(Text(pr, "targetRefName")),
                        author = Text(pr, "createdBy", "displayName"),
                        createdAt = Text(pr, "creationDate"),
                        url = MergeUrl(Text(pr, "repository", "webUrl"), Text(pr, "pullRequestId")),
                    }).ToList(),
                };
            }

            case "view-pr":
                if (number is null) return NeedsNumber();
                {
                    var result = await SendAsync(HttpMethod.Get, $"{repo}/pullrequests/{number}?api-version=7.1", headers, null, token);
                    return result.Ok
                        ? new
                        {
                            provider = at.Provider, url = result.Url, statusCode = result.StatusCode,
                            number = Int(result.Data, "pullRequestId"), title = Text(result.Data, "title"),
                            state = Text(result.Data, "status"), draft = Bool(result.Data, "isDraft"),
                            description = Text(result.Data, "description"),
                            sourceBranch = TrimRefs(Text(result.Data, "sourceRefName")),
                            targetBranch = TrimRefs(Text(result.Data, "targetRefName")),
                            author = Text(result.Data, "createdBy", "displayName"),
                            mergeStatus = Text(result.Data, "mergeStatus"),
                            lastMergeSourceCommit = Text(result.Data, "lastMergeSourceCommit", "commitId"),
                            hasConflicts = Text(result.Data, "mergeStatus") is "conflicts",
                            webUrl = MergeUrl(Text(result.Data, "repository", "webUrl"), Text(result.Data, "pullRequestId")),
                            data = result.Data,
                        }
                        : Describe(result, at, verb);
                }

            case "create-pr":
                if (!confirm) return NeedsConfirm("Creating a pull request is visible to others.");
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
                    return new { error = "sourceBranch and targetBranch are required." };
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        sourceRefName = Ref(sourceBranch!),
                        targetRefName = Ref(targetBranch!),
                        title,
                        description = body ?? string.Empty,
                    }, Json);

                    var result = await SendAsync(HttpMethod.Post, $"{repo}/pullrequests?api-version=7.1", headers, payload, token);
                    return result.Ok
                        ? new
                        {
                            provider = at.Provider, created = true, url = result.Url, statusCode = result.StatusCode,
                            number = Int(result.Data, "pullRequestId"), title = Text(result.Data, "title"),
                            webUrl = MergeUrl(Text(result.Data, "repository", "webUrl"), Text(result.Data, "pullRequestId")),
                        }
                        : Describe(result, at, verb);
                }

            case "comment-pr":
                if (!confirm) return NeedsConfirm("Commenting on a pull request is visible to others.");
                if (number is null) return NeedsNumber();
                if (string.IsNullOrWhiteSpace(body)) return new { error = "A comment body is required." };
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        comments = new[] { new { parentCommentId = 0, content = body, commentType = 1 } },
                        status = 1,
                    }, Json);

                    var result = await SendAsync(HttpMethod.Post, $"{repo}/pullRequests/{number}/threads?api-version=7.1", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, commented = true, number, threadId = Int(result.Data, "id"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "merge-pr":
                if (!confirm) return NeedsConfirm("Merging is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    // Azure needs the source commit it is completing into the target.
                    var current = await SendAsync(HttpMethod.Get, $"{repo}/pullrequests/{number}?api-version=7.1", headers, null, token);
                    if (!current.Ok) return Describe(current, at, verb);

                    var commit = Text(current.Data, "lastMergeSourceCommit", "commitId");
                    if (string.IsNullOrWhiteSpace(commit))
                        return new { error = "Azure DevOps did not report the source commit of this pull request.", number };

                    var payload = JsonSerializer.Serialize(new
                    {
                        status = "completed",
                        lastMergeSourceCommit = new { commitId = commit },
                    }, Json);

                    var result = await SendAsync(HttpMethod.Patch, $"{repo}/pullrequests/{number}?api-version=7.1", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, merged = true, number, state = Text(result.Data, "status"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "review-pr":
                if (!confirm) return NeedsConfirm("Posting a review is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var review = ReviewEvent(reviewEvent);
                    if (review is null) return BadReviewEvent();

                    if (review == "comment")
                    {
                        if (string.IsNullOrWhiteSpace(body)) return NeedsReviewBody();
                        var commentPayload = JsonSerializer.Serialize(new
                        {
                            comments = new[] { new { parentCommentId = 0, content = body, commentType = 1 } },
                            status = 1,
                        }, Json);

                        var result = await SendAsync(HttpMethod.Post, $"{repo}/pullRequests/{number}/threads?api-version=7.1", headers, commentPayload, token);
                        return result.Ok
                            ? new { provider = at.Provider, reviewed = true, reviewEvent = review, number, threadId = Int(result.Data, "id"), url = result.Url, statusCode = result.StatusCode }
                            : Describe(result, at, verb);
                    }

                    // A vote is cast by the authenticated identity, so its reviewer id comes from
                    // connectionData; the caller must already be a reviewer of the pull request.
                    var me = await SendAsync(HttpMethod.Get,
                        $"{at.BaseUrl}/{Uri.EscapeDataString(at.Project)}/_apis/connectionData?api-version=7.1-preview.1",
                        headers, null, token);
                    if (!me.Ok) return Describe(me, at, verb);

                    var reviewer = Text(me.Data, "authenticatedUser", "id");
                    if (string.IsNullOrWhiteSpace(reviewer))
                        return new
                        {
                            error = "Azure DevOps did not report the authenticated user id, so no review vote could be cast.",
                            url = me.Url,
                            hint = "Check that the PAT belongs to a user with access to this repository.",
                        };

                    var vote = review == "approve" ? 10 : -10;
                    var payload = JsonSerializer.Serialize(new { vote }, Json);
                    var voted = await SendAsync(HttpMethod.Put,
                        $"{repo}/pullrequests/{number}/reviewers/{reviewer}?api-version=7.1", headers, payload, token);

                    return voted.Ok
                        ? new { provider = at.Provider, reviewed = true, reviewEvent = review, number, vote, reviewer, url = voted.Url, statusCode = voted.StatusCode }
                        : Describe(voted, at, verb);
                }

            case "close-pr":
            case "reopen-pr":
                if (!confirm) return NeedsConfirm(verb == "close-pr"
                    ? "Abandoning a pull request is visible to others."
                    : "Reactivating a pull request is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var payload = JsonSerializer.Serialize(new { status = verb == "close-pr" ? "abandoned" : "active" }, Json);
                    var result = await SendAsync(HttpMethod.Patch, $"{repo}/pullrequests/{number}?api-version=7.1", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, number, action = verb, state = Text(result.Data, "status"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "update-pr":
                if (!confirm) return NeedsConfirm("Editing a pull request is visible to others.");
                if (number is null) return NeedsNumber();
                if (string.IsNullOrWhiteSpace(title) && body is null)
                    return new { error = "Pass title, body (the description), or both." };
                {
                    var fields = new Dictionary<string, object>();
                    if (!string.IsNullOrWhiteSpace(title)) fields["title"] = title!;
                    if (body is not null) fields["description"] = body;

                    var payload = JsonSerializer.Serialize(fields, Json);
                    var result = await SendAsync(HttpMethod.Patch, $"{repo}/pullrequests/{number}?api-version=7.1", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, updated = true, number, title = Text(result.Data, "title"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "list-issues":
            {
                var wiql = JsonSerializer.Serialize(new
                {
                    query = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project " +
                            "AND [System.State] <> 'Closed' AND [System.State] <> 'Removed' " +
                            "ORDER BY [System.ChangedDate] DESC",
                }, Json);

                var ids = await SendAsync(HttpMethod.Post,
                    $"{at.BaseUrl}/{Uri.EscapeDataString(at.Project)}/_apis/wit/wiql?api-version=7.1&$top={limit}",
                    headers, wiql, token);
                if (!ids.Ok) return Describe(ids, at, verb);

                var workItemIds = ids.Data.ValueKind == JsonValueKind.Object && ids.Data.TryGetProperty("workItems", out var items) &&
                                  items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().Take(limit).Select(w => Int(w, "id")).Where(id => id is not null).ToList()
                    : [];

                if (workItemIds.Count == 0)
                    return new { provider = at.Provider, url = ids.Url, count = 0, items = Array.Empty<object>() };

                var details = await SendAsync(HttpMethod.Get,
                    $"{at.BaseUrl}/{Uri.EscapeDataString(at.Project)}/_apis/wit/workitems?ids={string.Join(',', workItemIds)}&api-version=7.1",
                    headers, null, token);
                if (!details.Ok) return Describe(details, at, verb);

                return new
                {
                    provider = at.Provider, url = details.Url, count = details.Items.Count,
                    items = details.Items.Select(w => new
                    {
                        number = Int(w, "id"),
                        title = Text(w, "fields", "System.Title"),
                        state = Text(w, "fields", "System.State"),
                        type = Text(w, "fields", "System.WorkItemType"),
                        assignedTo = Text(w, "fields", "System.AssignedTo", "displayName"),
                        updatedAt = Text(w, "fields", "System.ChangedDate"),
                        url = Text(w, "_links", "html", "href"),
                    }).ToList(),
                };
            }

            case "create-issue":
                if (!confirm) return NeedsConfirm("Creating a work item is visible to others.");
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                {
                    var patch = JsonSerializer.Serialize(new object[]
                    {
                        new { op = "add", path = "/fields/System.Title", value = title },
                        new { op = "add", path = "/fields/System.Description", value = body ?? string.Empty },
                    }, Json);

                    // The work-item create endpoint speaks JSON-Patch, not JSON.
                    var result = await SendAsync(HttpMethod.Post,
                        $"{at.BaseUrl}/{Uri.EscapeDataString(at.Project)}/_apis/wit/workitems/$Issue?api-version=7.1",
                        headers, patch, token, "application/json-patch+json");

                    return result.Ok
                        ? new { provider = at.Provider, created = true, number = Int(result.Data, "id"), webUrl = Text(result.Data, "_links", "html", "href"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "pipelines":
            {
                var result = await SendAsync(HttpMethod.Get,
                    $"{at.BaseUrl}/{Uri.EscapeDataString(at.Project)}/_apis/build/builds?api-version=7.1&$top={limit}&queryOrder=finishTimeDescending",
                    headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(b => new
                    {
                        id = Int(b, "id"),
                        number = Text(b, "buildNumber"),
                        status = Text(b, "status"),
                        result = Text(b, "result"),
                        branch = TrimRefs(Text(b, "sourceBranch")),
                        definition = Text(b, "definition", "name"),
                        finishedAt = Text(b, "finishTime"),
                        webUrl = Text(b, "_links", "web", "href"),
                    }).ToList(),
                };
            }

            default:
                return UnknownAction(at.Provider);
        }
    }

    // ── Bitbucket Cloud ──────────────────────────────────────────────────────
    private async Task<object> BitbucketAsync(string verb, HostCoordinates at, HostCredential credential,
        int? number, string? title, string? body, string? sourceBranch, string? targetBranch,
        string state, int limit, bool confirm, CancellationToken token, string? reviewEvent)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = credential.Value };
        var repo = $"{at.BaseUrl}/2.0/repositories/{Uri.EscapeDataString(at.Project)}/{Uri.EscapeDataString(at.Repository)}";

        switch (verb)
        {
            case "list-prs":
            {
                var wanted = state switch { "open" or "opened" => "OPEN", "merged" => "MERGED", "declined" or "closed" => "DECLINED", "all" => "OPEN", _ => state.ToUpperInvariant() };
                var result = await SendAsync(HttpMethod.Get, $"{repo}/pullrequests?state={wanted}&pagelen={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, host = at.Host, project = at.Project, repository = at.Repository,
                    state = wanted, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(pr => new
                    {
                        number = Int(pr, "id"),
                        title = Text(pr, "title"),
                        state = Text(pr, "state"),
                        draft = Bool(pr, "draft"),
                        sourceBranch = Text(pr, "source", "branch", "name"),
                        targetBranch = Text(pr, "destination", "branch", "name"),
                        author = Text(pr, "author", "display_name"),
                        updatedAt = Text(pr, "updated_on"),
                        url = Text(pr, "links", "html", "href"),
                    }).ToList(),
                };
            }

            case "view-pr":
                if (number is null) return NeedsNumber();
                {
                    var result = await SendAsync(HttpMethod.Get, $"{repo}/pullrequests/{number}", headers, null, token);
                    return result.Ok
                        ? new
                        {
                            provider = at.Provider, url = result.Url, statusCode = result.StatusCode,
                            number = Int(result.Data, "id"), title = Text(result.Data, "title"),
                            state = Text(result.Data, "state"), draft = Bool(result.Data, "draft"),
                            description = Text(result.Data, "description"),
                            sourceBranch = Text(result.Data, "source", "branch", "name"),
                            targetBranch = Text(result.Data, "destination", "branch", "name"),
                            author = Text(result.Data, "author", "display_name"),
                            mergeCommit = Text(result.Data, "merge_commit", "hash"),
                            webUrl = Text(result.Data, "links", "html", "href"),
                            data = result.Data,
                        }
                        : Describe(result, at, verb);
                }

            case "create-pr":
                if (!confirm) return NeedsConfirm("Creating a pull request is visible to others.");
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
                    return new { error = "sourceBranch and targetBranch are required." };
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        title,
                        description = body ?? string.Empty,
                        source = new { branch = new { name = sourceBranch } },
                        destination = new { branch = new { name = targetBranch } },
                    }, Json);

                    var result = await SendAsync(HttpMethod.Post, $"{repo}/pullrequests", headers, payload, token);
                    return result.Ok
                        ? new
                        {
                            provider = at.Provider, created = true, url = result.Url, statusCode = result.StatusCode,
                            number = Int(result.Data, "id"), title = Text(result.Data, "title"),
                            webUrl = Text(result.Data, "links", "html", "href"),
                        }
                        : Describe(result, at, verb);
                }

            case "comment-pr":
                if (!confirm) return NeedsConfirm("Commenting on a pull request is visible to others.");
                if (number is null) return NeedsNumber();
                if (string.IsNullOrWhiteSpace(body)) return new { error = "A comment body is required." };
                {
                    var payload = JsonSerializer.Serialize(new { content = new { raw = body } }, Json);
                    var result = await SendAsync(HttpMethod.Post, $"{repo}/pullrequests/{number}/comments", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, commented = true, number, id = Int(result.Data, "id"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "merge-pr":
                if (!confirm) return NeedsConfirm("Merging is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var result = await SendAsync(HttpMethod.Post, $"{repo}/pullrequests/{number}/merge", headers, "{}", token);
                    return result.Ok
                        ? new { provider = at.Provider, merged = true, number, state = Text(result.Data, "state"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "review-pr":
                if (!confirm) return NeedsConfirm("Posting a review is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var review = ReviewEvent(reviewEvent);
                    if (review is null) return BadReviewEvent();

                    if (review == "comment")
                    {
                        if (string.IsNullOrWhiteSpace(body)) return NeedsReviewBody();
                        var payload = JsonSerializer.Serialize(new { content = new { raw = body } }, Json);
                        var result = await SendAsync(HttpMethod.Post, $"{repo}/pullrequests/{number}/comments", headers, payload, token);
                        return result.Ok
                            ? new { provider = at.Provider, reviewed = true, reviewEvent = review, number, id = Int(result.Data, "id"), url = result.Url, statusCode = result.StatusCode }
                            : Describe(result, at, verb);
                    }

                    var endpoint = review == "approve" ? "approve" : "request-changes";
                    var voted = await SendAsync(HttpMethod.Post, $"{repo}/pullrequests/{number}/{endpoint}", headers, "{}", token);
                    return voted.Ok
                        ? new
                        {
                            provider = at.Provider, reviewed = true, reviewEvent = review, number,
                            reviewer = Text(voted.Data, "user", "display_name"), url = voted.Url, statusCode = voted.StatusCode,
                        }
                        : Describe(voted, at, verb);
                }

            case "close-pr":
            case "reopen-pr":
                if (!confirm) return NeedsConfirm(verb == "close-pr"
                    ? "Declining a pull request is visible to others."
                    : "Reopening a pull request is visible to others.");
                if (number is null) return NeedsNumber();
                {
                    var payload = JsonSerializer.Serialize(new { state = verb == "close-pr" ? "DECLINED" : "OPEN" }, Json);
                    var result = await SendAsync(HttpMethod.Put, $"{repo}/pullrequests/{number}", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, number, action = verb, state = Text(result.Data, "state"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "update-pr":
                if (!confirm) return NeedsConfirm("Editing a pull request is visible to others.");
                if (number is null) return NeedsNumber();
                if (string.IsNullOrWhiteSpace(title) && body is null)
                    return new { error = "Pass title, body (the description), or both." };
                {
                    var fields = new Dictionary<string, object>();
                    if (!string.IsNullOrWhiteSpace(title)) fields["title"] = title!;
                    if (body is not null) fields["description"] = body;

                    var payload = JsonSerializer.Serialize(fields, Json);
                    var result = await SendAsync(HttpMethod.Put, $"{repo}/pullrequests/{number}", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, updated = true, number, title = Text(result.Data, "title"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "list-issues":
            {
                var wanted = state switch { "open" or "opened" => "new", "all" => "all", _ => state };
                var result = await SendAsync(HttpMethod.Get, $"{repo}/issues?state={Uri.EscapeDataString(wanted)}&pagelen={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(issue => new
                    {
                        number = Int(issue, "id"),
                        title = Text(issue, "title"),
                        state = Text(issue, "state"),
                        kind = Text(issue, "kind"),
                        priority = Text(issue, "priority"),
                        reporter = Text(issue, "reporter", "display_name"),
                        updatedAt = Text(issue, "updated_on"),
                        url = Text(issue, "links", "html", "href"),
                    }).ToList(),
                };
            }

            case "create-issue":
                if (!confirm) return NeedsConfirm("Creating an issue is visible to others.");
                if (string.IsNullOrWhiteSpace(title)) return new { error = "A title is required." };
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        title,
                        content = new { raw = body ?? string.Empty },
                    }, Json);

                    var result = await SendAsync(HttpMethod.Post, $"{repo}/issues", headers, payload, token);
                    return result.Ok
                        ? new { provider = at.Provider, created = true, number = Int(result.Data, "id"), webUrl = Text(result.Data, "links", "html", "href"), url = result.Url, statusCode = result.StatusCode }
                        : Describe(result, at, verb);
                }

            case "pipelines":
            {
                var result = await SendAsync(HttpMethod.Get, $"{repo}/pipelines?sort=-created_on&pagelen={limit}", headers, null, token);
                if (!result.Ok) return Describe(result, at, verb);

                return new
                {
                    provider = at.Provider, url = result.Url, count = result.Items.Count,
                    items = result.Items.Select(p => new
                    {
                        id = Int(p, "build_number"),
                        status = Text(p, "state", "name"),
                        result = Text(p, "state", "result", "name"),
                        target = Text(p, "target", "ref_name"),
                        createdAt = Text(p, "created_on"),
                        webUrl = Text(p, "links", "html", "href"),
                    }).ToList(),
                };
            }

            default:
                return UnknownAction(at.Provider);
        }
    }

    // ── resolution ───────────────────────────────────────────────────────────
    private async Task<(HostCoordinates? Coordinates, string? RemoteUrl, object? Error)> ResolveAsync(
        string provider, string? hostUrl, string? project, string? repository, string? repoPath)
    {
        var requested = (provider ?? "auto").Trim().ToLowerInvariant();

        // Fully specified coordinates need no repository: git_host works from a directory that is not
        // a checkout at all (a scheduler, a build agent, a client that only names the project).
        var fullySpecified = requested is GitLab or AzureDevOps or Bitbucket &&
                             !string.IsNullOrWhiteSpace(project) &&
                             !string.IsNullOrWhiteSpace(repository) &&
                             (!string.IsNullOrWhiteSpace(hostUrl) || requested == Bitbucket);

        if (fullySpecified)
        {
            var direct = ParseRemote(null, requested, hostUrl, project, repository);
            return direct.Error is not null ? (null, null, direct.Error) : (direct.Coordinates, null, null);
        }

        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath, _workspace?.LoadedSolutionPath);
        if (error is not null) return (null, null, error);

        var remoteUrl = await RemoteUrlAsync(repoDir!);
        var parsed = ParseRemote(remoteUrl, provider, hostUrl, project, repository);
        return parsed.Error is not null ? (null, remoteUrl, parsed.Error) : (parsed.Coordinates, remoteUrl, null);
    }

    private static async Task<string?> RemoteUrlAsync(string repoDir)
    {
        var (exitCode, _, stdout, _, _) = await GitCli.RunAsync(repoDir, "remote get-url origin", 20);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout)) return stdout.Trim();

        var (listExit, _, list, _, _) = await GitCli.RunAsync(repoDir, "remote", 20);
        var first = list.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (listExit != 0 || first is null) return null;

        var (getExit, _, url, _, _) = await GitCli.RunAsync(repoDir, $"remote get-url {first}", 20);
        return getExit == 0 ? url.Trim() : null;
    }

    /// <summary>
    /// Reads the provider, host and coordinates out of a git remote URL (https, ssh:// and scp-like
    /// forms) or out of explicit arguments, which win over what the remote says.
    /// </summary>
    public static (HostCoordinates? Coordinates, object? Error) ParseRemote(
        string? remoteUrl, string? provider, string? hostUrl, string? project, string? repository)
    {
        var requested = (provider ?? "auto").Trim().ToLowerInvariant();
        var parsed = remoteUrl is null ? null : SplitRemote(remoteUrl);
        var host = hostUrl;

        if (parsed is not null)
        {
            host ??= parsed.Value.Host;

            if (requested == "auto")
            {
                var probe = (parsed.Value.Host + "/" + parsed.Value.Path).ToLowerInvariant();
                requested = probe.Contains("github.com") ? GitHub
                    : probe.Contains("gitlab") ? GitLab
                    : probe.Contains("dev.azure.com") || probe.Contains("visualstudio.com") ? AzureDevOps
                    : probe.Contains("bitbucket") ? Bitbucket
                    : "auto";
            }
        }

        if (requested == "auto")
            return (null, new
            {
                error = "Could not tell the hosting provider from the git remote. Pass provider (gitlab, azuredevops, bitbucket) and hostUrl.",
                remoteUrl,
                supported = Providers,
            });

        if (requested == GitHub)
            return (null, new
            {
                error = "GitHub is covered by the gh_* tools (gh_pr, gh_issue, gh_actions), which use the authenticated gh CLI.",
                provider = GitHub,
            });

        if (!Providers.Contains(requested))
            return (null, new { error = $"Unknown provider '{requested}'. Use gitlab, azuredevops or bitbucket.", supported = Providers });

        var path = parsed?.Path ?? string.Empty;
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Azure DevOps carries the organization in the host and the project in the path.
        if (requested == AzureDevOps)
        {
            string org;
            string azureProject;
            var azureRepo = repository;

            // SSH remotes use ssh.dev.azure.com:v3/{org}/{project}/{repo}; https uses
            // dev.azure.com/{org}/{project}/_git/{repo} and the legacy {org}.visualstudio.com form.
            if (segments.Count > 0 && segments[0].Equals("v3", StringComparison.OrdinalIgnoreCase))
                segments.RemoveAt(0);

            // The `_git` separator is not part of a coordinate; dropping it leaves the project and the
            // repository adjacent in every form (org/project/repo under dev.azure.com, project/repo
            // under {org}.visualstudio.com).
            segments.RemoveAll(segment => segment.Equals("_git", StringComparison.OrdinalIgnoreCase));

            if (parsed is not null && parsed.Value.Host.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                org = segments.Count > 0 ? segments[0] : string.Empty;
                azureProject = segments.Count > 1 ? segments[1] : string.Empty;
                azureRepo ??= segments.Count > 2 ? segments[2] : null;
                host ??= "https://dev.azure.com";
                host = host!.Replace("{org}", org, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            }
            else
            {
                // https://{org}.visualstudio.com/{project}/_git/{repo}
                var visualStudioHost = parsed?.Host ?? string.Empty;
                org = project ?? (visualStudioHost.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
                    ? visualStudioHost[..visualStudioHost.IndexOf('.')]
                    : string.Empty);
                azureProject = segments.Count > 0 ? segments[0] : string.Empty;
                azureRepo ??= segments.Count > 1 ? segments[1] : null;
                host ??= visualStudioHost.Length > 0 ? $"https://{visualStudioHost}" : null;
            }

            azureProject = string.IsNullOrWhiteSpace(project) ? azureProject : project!;

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(azureProject) || string.IsNullOrWhiteSpace(azureRepo))
                return (null, new
                {
                    error = "Azure DevOps needs hostUrl (https://dev.azure.com/{organization}, or the https://{org}.visualstudio.com form), project and repository. Pass what the remote does not provide.",
                    remoteUrl,
                    parsed = parsed is null ? null : new { parsed.Value.Host, parsed.Value.Path },
                });

            return (new HostCoordinates(AzureDevOps, UriHost(host!), NormalizeBase(host!), azureProject, azureRepo!), null);
        }

        // GitLab (nested groups) and Bitbucket (workspace/repo): the whole path minus the repo is the project.
        var repoName = repository ?? (segments.Count > 0 ? segments[^1] : null);
        var projectPath = project ?? (segments.Count > 1 ? string.Join('/', segments.Take(segments.Count - 1)) : null);

        if (repoName is not null) repoName = repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repoName[..^4] : repoName;

        host ??= DefaultHost(requested);

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(repoName))
            return (null, new
            {
                error = "The remote does not name the host, the project and the repository. Pass hostUrl, project and repository.",
                remoteUrl,
            });

        // Bitbucket Cloud always talks to its API host; a self-hosted Bitbucket Server needs hostUrl.
        var baseUrl = requested == Bitbucket
            ? (string.IsNullOrWhiteSpace(hostUrl) ? "https://api.bitbucket.org" : NormalizeBase(hostUrl!))
            : NormalizeBase(host!);

        return (new HostCoordinates(requested, UriHost(host!), baseUrl, projectPath, repoName!), null);
    }

    private static string NormalizeBase(string host) =>
        host.Contains("://", StringComparison.Ordinal) ? host.TrimEnd('/') : "https://" + host.TrimEnd('/');

    private static (string Host, string Path)? SplitRemote(string url)
    {
        var value = url.Trim();

        // scp-like: git@host:group/repo.git
        var at = value.IndexOf('@');
        var colon = at >= 0 ? value.IndexOf(':', at) : -1;
        if (at > 0 && colon > at && !value.Contains("://", StringComparison.Ordinal))
            return (value[(at + 1)..colon], value[(colon + 1)..]);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return (uri.Host, uri.AbsolutePath);
    }

    private static string UriHost(string hostUrl) =>
        Uri.TryCreate(hostUrl, UriKind.Absolute, out var uri) ? uri.Host : hostUrl;

    // ── credentials ──────────────────────────────────────────────────────────
    private sealed record HostCredential(string Value, string EnvVar);

    private static HostCredential? Credential(string provider) => provider switch
    {
        GitLab => Env("GITLAB_TOKEN") is { } gl ? new HostCredential(gl, "GITLAB_TOKEN") : null,
        AzureDevOps => (Env("AZURE_DEVOPS_EXT_PAT") ?? Env("AZURE_DEVOPS_TOKEN")) is { } pat
            ? new HostCredential("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pat)),
                Env("AZURE_DEVOPS_EXT_PAT") is not null ? "AZURE_DEVOPS_EXT_PAT" : "AZURE_DEVOPS_TOKEN")
            : null,
        Bitbucket => Env("BITBUCKET_TOKEN") is { } token
            ? new HostCredential("Bearer " + token, "BITBUCKET_TOKEN")
            : Env("BITBUCKET_USERNAME") is { } user && Env("BITBUCKET_APP_PASSWORD") is { } password
                ? new HostCredential("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)),
                    "BITBUCKET_USERNAME + BITBUCKET_APP_PASSWORD")
                : null,
        _ => null,
    };

    private static object AuthStatus(string provider)
    {
        var credential = Credential(provider);
        return new
        {
            configured = credential is not null,
            envVar = provider switch
            {
                GitLab => "GITLAB_TOKEN",
                AzureDevOps => "AZURE_DEVOPS_EXT_PAT (or AZURE_DEVOPS_TOKEN)",
                _ => "BITBUCKET_TOKEN (or BITBUCKET_USERNAME + BITBUCKET_APP_PASSWORD)",
            },
        };
    }

    private static object MissingCredential(string provider) => new
    {
        error = $"No credential for {provider}. Set {provider switch
        {
            GitLab => "GITLAB_TOKEN",
            AzureDevOps => "AZURE_DEVOPS_EXT_PAT (or AZURE_DEVOPS_TOKEN)",
            _ => "BITBUCKET_TOKEN, or BITBUCKET_USERNAME + BITBUCKET_APP_PASSWORD",
        }} in the server's environment.",
        provider,
        auth = AuthStatus(provider),
    };

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────
    private sealed record HostResponse(
        string Url, int StatusCode, bool Ok, JsonElement Data, List<JsonElement> Items, string Text);

    private async Task<HostResponse> SendAsync(HttpMethod method, string url,
        Dictionary<string, string> headers, string? jsonBody, CancellationToken token,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("VisualMCP/1.0");

        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, contentType);

        using var response = await _http.SendAsync(request, token);
        var text = await response.Content.ReadAsStringAsync(token);

        JsonElement data = default;
        var items = new List<JsonElement>();

        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                data = document.RootElement.Clone();

                if (data.ValueKind == JsonValueKind.Array)
                    items.AddRange(data.EnumerateArray());
                else if (data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                        items.AddRange(value.EnumerateArray());       // Azure DevOps
                    else if (data.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                        items.AddRange(values.EnumerateArray());      // Bitbucket
                }
            }
            catch (JsonException) { /* not JSON: the raw text is returned in the error */ }
        }

        return new HostResponse(url, (int)response.StatusCode, response.IsSuccessStatusCode, data, items,
            Truncate(text, 8000));
    }

    private static object Describe(HostResponse response, HostCoordinates at, string action) => new
    {
        provider = at.Provider,
        host = at.Host,
        action,
        url = response.Url,
        statusCode = response.StatusCode,
        succeeded = false,
        error = response.Text.Length == 0
            ? $"The {at.Provider} API answered {response.StatusCode}."
            : $"The {at.Provider} API answered {response.StatusCode}: {response.Text}",
        hint = response.StatusCode switch
        {
            401 or 403 => "Check the token's scopes (and that it has not expired) in the server's environment.",
            404 => "Check project and repository (and the pull request number / issue id).",
            _ => null,
        },
        data = response.Data.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : response.Data,
    };

    private static object NeedsNumber() => new { error = "A pull-request or issue number is required." };

    /// <summary>The three review events, or null when the value names none of them.</summary>
    private static string? ReviewEvent(string? value) => (value ?? "comment").Trim().ToLowerInvariant() switch
    {
        "approve" or "approved" => "approve",
        "request-changes" or "request_changes" or "requestchanges" or "changes" => "request-changes",
        "comment" or "note" => "comment",
        _ => null,
    };

    private static object BadReviewEvent() => new
    {
        error = "reviewEvent must be approve, request-changes or comment.",
        hint = "Leave it out to post a comment: 'comment' needs a body, 'approve' does not.",
    };

    private static object NeedsReviewBody() => new { error = "A review comment needs a body (reviewEvent=comment)." };

    private static object NeedsConfirm(string what) => new { error = $"{what} Pass confirm=true to proceed." };

    private static object UnknownAction(string provider) => new
    {
        error = "Unknown action. Use detect, list-prs, view-pr, create-pr, comment-pr, merge-pr, review-pr, " +
                "close-pr, reopen-pr, update-pr, list-issues, create-issue or pipelines.",
        provider,
    };

    private static object UnsupportedProvider(string provider) => new
    {
        error = $"Unknown provider '{provider}'. Use gitlab, azuredevops or bitbucket.",
        supported = Providers,
    };

    // ── JSON helpers (a missing field is null, never an exception) ───────────
    private static string? Text(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? Int(JsonElement element, params string[] path)
    {
        var text = Text(element, path);
        if (text is not null && int.TryParse(text, out var number)) return number;

        var current = element;
        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var value) ? value : null;
    }

    private static bool? Bool(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static List<string> Strings(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
                return [];
        }

        return current.ValueKind == JsonValueKind.Array
            ? current.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];
    }

    private static string? TrimRefs(string? reference) =>
        reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? reference["refs/heads/".Length..] : reference;

    private static string Ref(string branch) => branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private static string? MergeUrl(string? repositoryWebUrl, string? number) =>
        repositoryWebUrl is null || number is null ? null : $"{repositoryWebUrl}/pullrequest/{number}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
