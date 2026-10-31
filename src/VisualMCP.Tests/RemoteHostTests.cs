using System.Net;
using System.Text;
using Snitch.Collaboration;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The remote-host layer: URL parsing is pure, and the HTTP side runs against a stub handler so the
/// request it builds (URL, header, body) is asserted without a network or a real token.
/// </summary>
public sealed class RemoteHostTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    // ── parsing ─────────────────────────────────────────────────────────────
    [Fact]
    public void Parse_HttpsGitLabUrl_SplitsNestedGroupsFromTheRepository()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote("https://gitlab.com/group/sub/proj.git", null, null, null, null);

        Assert.Null(error);
        Assert.Equal(RemoteHostService.GitLab, coordinates!.Provider);
        Assert.Equal("gitlab.com", coordinates.Host);
        Assert.Equal("group/sub", coordinates.Project);
        Assert.Equal("proj", coordinates.Repository);
        Assert.Equal("https://gitlab.com", coordinates.BaseUrl);
    }

    [Fact]
    public void Parse_ScpStyleGitLabUrl_WorksToo()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote("git@gitlab.com:group/proj.git", null, null, null, null);

        Assert.Null(error);
        Assert.Equal(RemoteHostService.GitLab, coordinates!.Provider);
        Assert.Equal("group", coordinates.Project);
        Assert.Equal("proj", coordinates.Repository);
    }

    [Fact]
    public void Parse_AzureDevOpsHttpsUrl_ReadsOrganizationProjectAndRepository()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote(
            "https://dev.azure.com/contoso/Web/_git/app", null, null, null, null);

        Assert.Null(error);
        Assert.Equal(RemoteHostService.AzureDevOps, coordinates!.Provider);
        Assert.Equal("https://dev.azure.com", coordinates.BaseUrl);
        Assert.Equal("Web", coordinates.Project);
        Assert.Equal("app", coordinates.Repository);
    }

    [Fact]
    public void Parse_AzureDevOpsSshUrl_SkipsTheV3Prefix()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote(
            "git@ssh.dev.azure.com:v3/contoso/Web/app", null, null, null, null);

        Assert.Null(error);
        Assert.Equal(RemoteHostService.AzureDevOps, coordinates!.Provider);
        Assert.Equal("Web", coordinates.Project);
        Assert.Equal("app", coordinates.Repository);
    }

    [Fact]
    public void Parse_BitbucketUrl_UsesTheApiHost()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote(
            "https://bitbucket.org/team/repo.git", null, null, null, null);

        Assert.Null(error);
        Assert.Equal(RemoteHostService.Bitbucket, coordinates!.Provider);
        Assert.Equal("team", coordinates.Project);
        Assert.Equal("repo", coordinates.Repository);
        Assert.Equal("https://api.bitbucket.org", coordinates.BaseUrl);
    }

    [Fact]
    public void Parse_GitHubRemote_IsRedirectedToTheGhTools()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote("https://github.com/owner/repo.git", null, null, null, null);

        Assert.Null(coordinates);
        Assert.Contains("gh_", ResultAssert.Error(error!));
    }

    [Fact]
    public void Parse_UnknownHost_ExplainsWhatToPass()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote("https://git.example.com/team/repo.git", null, null, null, null);

        Assert.Null(coordinates);
        Assert.Contains("provider", ResultAssert.Error(error!));
    }

    [Fact]
    public void Parse_ExplicitArguments_WinOverTheRemote()
    {
        var (coordinates, error) = RemoteHostService.ParseRemote(
            "https://gitlab.com/group/proj.git", "bitbucket", "https://example.bitbucket.io", "other-workspace", "other-repo");

        Assert.Null(error);
        Assert.Equal(RemoteHostService.Bitbucket, coordinates!.Provider);
        Assert.Equal("other-workspace", coordinates.Project);
        Assert.Equal("other-repo", coordinates.Repository);
        Assert.Equal("https://example.bitbucket.io", coordinates.BaseUrl);
    }

    // ── HTTP ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GitLab_ListPrs_SendsTheTokenAndNormalizesTheItems()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK,
            """[{"iid":7,"title":"Fix the thing","state":"opened","draft":false,"source_branch":"feature","target_branch":"main","author":{"name":"Ana"},"updated_at":"2026-10-31T08:00:00Z","web_url":"https://gitlab.example.com/group/sub/proj/-/merge_requests/7"}]""");

        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "test-token");

        var result = await service.HostAsync("list-prs", "gitlab", "https://gitlab.example.com", "group/sub", "proj",
            number: null, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("count").GetInt32());

        var item = json.GetProperty("items")[0];
        Assert.Equal(7, item.GetProperty("number").GetInt32());
        Assert.Equal("feature", item.GetProperty("sourceBranch").GetString());
        Assert.Equal("Ana", item.GetProperty("author").GetString());

        var request = handler.LastRequest!;
        Assert.Equal("test-token", request.Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Contains("/api/v4/projects/", request.RequestUri!.ToString());
        Assert.Contains("merge_requests", request.RequestUri.ToString());
        Assert.Contains("state=opened", request.RequestUri.ToString());
    }

    [Fact]
    public async Task GitLab_CreatePr_NeedsConfirmation()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.Created, """{"iid":9,"title":"x","web_url":"https://gitlab.example.com/mr/9"}""");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "test-token");

        var refusal = await service.HostAsync("create-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: null, title: "New flow", body: null, sourceBranch: "feature", targetBranch: "main",
            state: null, limit: 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30);

        Assert.Contains("confirm=true", ResultAssert.Error(refusal));
        Assert.Null(handler.LastRequest);

        var created = await service.HostAsync("create-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: null, title: "New flow", body: "why", sourceBranch: "feature", targetBranch: "main",
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30);

        Assert.Equal(9, ResultAssert.Json(created).GetProperty("number").GetInt32());
        Assert.Contains("\"source_branch\":\"feature\"", handler.LastBody);
    }

    [Fact]
    public async Task Azure_ListPrs_BuildsThePullRequestsEndpoint()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"value":[{"pullRequestId":12,"title":"Ship it","status":"active","isDraft":false,"sourceRefName":"refs/heads/feature","targetRefName":"refs/heads/main","createdBy":{"displayName":"Ana"},"lastMergeSourceCommit":{"commitId":"abc123"},"repository":{"webUrl":"https://dev.azure.com/contoso/Web/_git/app"}}]}""");

        var service = new RemoteHostService(handler);

        using var pat = new EnvScope("AZURE_DEVOPS_EXT_PAT", "pat-value");

        var result = await service.HostAsync("list-prs", "azuredevops", "https://dev.azure.com", "Web", "app",
            null, null, null, null, null, null, 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal(12, json.GetProperty("items")[0].GetProperty("number").GetInt32());
        Assert.Equal("feature", json.GetProperty("items")[0].GetProperty("sourceBranch").GetString());

        Assert.Contains("/Web/_apis/git/repositories/app/pullrequests", handler.LastRequest!.RequestUri!.ToString());
        Assert.StartsWith("Basic ", handler.LastRequest.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Bitbucket_ListPrs_BuildsTheApiEndpoint()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"values":[{"id":3,"title":"Fix","state":"OPEN","draft":false,"source":{"branch":{"name":"feature"}},"destination":{"branch":{"name":"main"}},"author":{"display_name":"Ana"},"links":{"html":{"href":"https://bitbucket.org/team/repo/pull-requests/3"}}}]}""");

        var service = new RemoteHostService(handler);

        using var user = new EnvScope("BITBUCKET_USERNAME", "ana");
        using var password = new EnvScope("BITBUCKET_APP_PASSWORD", "secret");

        var result = await service.HostAsync("list-prs", "bitbucket", null, "team", "repo",
            null, null, null, null, null, null, 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal(3, json.GetProperty("items")[0].GetProperty("number").GetInt32());

        Assert.Contains("api.bitbucket.org/2.0/repositories/team/repo/pullrequests", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GitLab_ApiFailure_IsReportedWithTheStatusAndTheBody()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "expired");

        var result = await service.HostAsync("list-prs", "gitlab", "https://gitlab.example.com", "group", "proj",
            null, null, null, null, null, null, 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(401, json.GetProperty("statusCode").GetInt32());
        Assert.Contains("401", json.GetProperty("error").GetString());
        Assert.Contains("scopes", json.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task MissingCredential_NamesTheEnvironmentVariable()
    {
        using var repo = new TestGitRepo();
        var service = new RemoteHostService(new StubHandler(HttpStatusCode.OK, "[]"));

        using var token = new EnvScope("GITLAB_TOKEN", null);

        var result = await service.HostAsync("list-prs", "gitlab", "https://gitlab.example.com", "group", "proj",
            null, null, null, null, null, null, 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30);

        var error = ResultAssert.Error(result);
        Assert.Contains("GITLAB_TOKEN", error);
    }

    /// <summary>
    /// Answers with a queued response per request, so a flow that needs two API calls (connectionData
    /// then the vote) is asserted in the order it happens.
    /// </summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) => _responses = new(responses);

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // ── reviews and the pull-request lifecycle ──────────────────────────────
    [Fact]
    public async Task GitLab_ReviewApprove_NeedsConfirmationThenPostsToApprove()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.Created, """{"id":5}""");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "test-token");

        var refusal = await service.HostAsync("review-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: 7, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: false, repoPath: repo.Directory, timeoutSeconds: 30, reviewEvent: "approve");

        Assert.Contains("confirm=true", ResultAssert.Error(refusal));
        Assert.Null(handler.LastRequest);

        var approved = await service.HostAsync("review-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: 7, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30, reviewEvent: "approve");

        var json = ResultAssert.Json(approved);
        Assert.True(json.GetProperty("approved").GetBoolean());
        Assert.Equal("approve", json.GetProperty("reviewEvent").GetString());

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("merge_requests/7/approve", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task GitLab_RequestChanges_IsReportedAsUnsupportedInsteadOfPretending()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "test-token");

        var result = await service.HostAsync("review-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: 7, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30, reviewEvent: "request-changes");

        var json = ResultAssert.Json(result);
        Assert.False(json.GetProperty("supported").GetBoolean());
        Assert.Contains("no request-changes", json.GetProperty("error").GetString());
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Azure_ReviewApprove_ReadsTheAuthenticatedUserThenCastsTheVote()
    {
        using var repo = new TestGitRepo();
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"authenticatedUser":{"id":"user-guid"}}"""),
            (HttpStatusCode.OK, """{"id":"user-guid","vote":10}"""));
        var service = new RemoteHostService(handler);

        using var pat = new EnvScope("AZURE_DEVOPS_EXT_PAT", "pat-value");

        var result = await service.HostAsync("review-pr", "azuredevops", "https://dev.azure.com", "Web", "app",
            number: 12, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30, reviewEvent: "approve");

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("reviewed").GetBoolean());
        Assert.Equal(10, json.GetProperty("vote").GetInt32());
        Assert.Equal("user-guid", json.GetProperty("reviewer").GetString());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("connectionData", handler.Requests[0].Url);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Contains("pullrequests/12/reviewers/user-guid", handler.Requests[1].Url);
        Assert.Contains("\"vote\":10", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Bitbucket_RequestChanges_PostsToTheRequestChangesEndpoint()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK, """{"user":{"display_name":"Ana"}}""");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("BITBUCKET_TOKEN", "test-token");

        var result = await service.HostAsync("review-pr", "bitbucket", null, "team", "repo",
            number: 3, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30, reviewEvent: "request-changes");

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("reviewed").GetBoolean());
        Assert.Equal("Ana", json.GetProperty("reviewer").GetString());

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("pullrequests/3/request-changes", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task GitLab_ClosePr_SendsTheStateEvent()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK, """{"state":"closed"}""");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "test-token");

        var result = await service.HostAsync("close-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: 7, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal("closed", json.GetProperty("state").GetString());

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Contains("merge_requests/7", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"state_event\":\"close\"", handler.LastBody);
    }

    [Fact]
    public async Task Bitbucket_ReopenPr_SendsOpen()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK, """{"state":"OPEN"}""");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("BITBUCKET_TOKEN", "test-token");

        var result = await service.HostAsync("reopen-pr", "bitbucket", null, "team", "repo",
            number: 3, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30);

        Assert.Equal("OPEN", ResultAssert.Json(result).GetProperty("state").GetString());
        Assert.Contains("\"state\":\"OPEN\"", handler.LastBody);
    }

    [Fact]
    public async Task Azure_UpdatePr_SendsTitleAndDescription()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK, """{"title":"New title","status":"active"}""");
        var service = new RemoteHostService(handler);

        using var pat = new EnvScope("AZURE_DEVOPS_EXT_PAT", "pat-value");

        var result = await service.HostAsync("update-pr", "azuredevops", "https://dev.azure.com", "Web", "app",
            number: 12, title: "New title", body: "why", sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30);

        Assert.Equal("New title", ResultAssert.Json(result).GetProperty("title").GetString());
        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Contains("\"title\":\"New title\"", handler.LastBody);
        Assert.Contains("\"description\":\"why\"", handler.LastBody);
    }

    [Fact]
    public async Task A_review_comment_needs_a_body()
    {
        using var repo = new TestGitRepo();
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var service = new RemoteHostService(handler);

        using var token = new EnvScope("GITLAB_TOKEN", "test-token");

        var result = await service.HostAsync("review-pr", "gitlab", "https://gitlab.example.com", "group", "proj",
            number: 7, title: null, body: null, sourceBranch: null, targetBranch: null,
            state: null, limit: 20, confirm: true, repoPath: repo.Directory, timeoutSeconds: 30, reviewEvent: "comment");

        Assert.Contains("needs a body", ResultAssert.Error(result));
        Assert.Null(handler.LastRequest);
    }

    /// <summary>Sets an environment variable for the test and restores the previous value after it.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
