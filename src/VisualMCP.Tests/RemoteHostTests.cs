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
