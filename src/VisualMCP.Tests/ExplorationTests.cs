using Snitch.Languages.CSharp.Navigation;
using Snitch.Languages.CSharp.Testing;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The exploration wave: endpoints, TODO comments, fuzzy file search, stack traces and the test
/// impact analysis (which needs git, so the fake workspace checks the refusal path).
/// </summary>
public sealed class ExplorationTests
{
    private const string ApiSource = """
        namespace Probe.Api;

        // TODO: add the pagination query parameters
        internal class OrdersController
        {
            [HttpGet("orders/{id}")]
            public string Get(int id) => id.ToString();

            [HttpPost("orders")]
            public string Create() => "ok";
        }

        internal static class Program
        {
            public static void Map(Microsoft.AspNetCore.Builder.WebApplication app)
            {
                app.MapGet("/health", () => "ok");
            }
        }
        """;

    [Fact]
    public async Task FindEndpoints_ReturnsControllersAndMinimalApis()
    {
        var workspace = FakeWorkspace.Create(("Api.cs", ApiSource));
        using var scope = workspace;
        var service = new WorkspaceExplorationService(workspace);

        var result = ResultAssert.Json(await service.FindEndpointsAsync(null, 100));

        Assert.Equal(3, result.GetProperty("count").GetInt32());
        var endpoints = result.GetProperty("endpoints").EnumerateArray().ToList();

        Assert.Contains(endpoints, endpoint =>
            endpoint.GetProperty("verb").GetString() == "GET" &&
            endpoint.GetProperty("route").GetString() == "orders/{id}" &&
            endpoint.GetProperty("method").GetString() == "Get");

        Assert.Contains(endpoints, endpoint =>
            endpoint.GetProperty("kind").GetString() == "minimalApi" &&
            endpoint.GetProperty("route").GetString() == "/health");
    }

    [Fact]
    public async Task FindTodos_ReturnsTheMarkerAndTheLine()
    {
        var workspace = FakeWorkspace.Create(("Api.cs", ApiSource));
        using var scope = workspace;
        var service = new WorkspaceExplorationService(workspace);

        var result = ResultAssert.Json(await service.FindTodosAsync(null, 100));
        Assert.Equal(1, result.GetProperty("count").GetInt32());

        var todo = result.GetProperty("todos")[0];
        Assert.Equal("TODO", todo.GetProperty("marker").GetString());
        Assert.Contains("pagination", todo.GetProperty("text").GetString());
        Assert.Equal(3, todo.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task FindFile_RanksCamelHumpsAndExactNames()
    {
        var workspace = FakeWorkspace.Create(
            ("InlineFieldService.cs", "namespace Probe; internal class A { }"),
            ("Orders.cs", "namespace Probe; internal class B { }"));
        using var scope = workspace;
        var service = new WorkspaceExplorationService(workspace);

        var humps = ResultAssert.Json(await service.FindFileAsync("IFS", 10));
        Assert.Contains(humps.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("name").GetString() == "InlineFieldService.cs");

        var exact = ResultAssert.Json(await service.FindFileAsync("Orders.cs", 10));
        Assert.Equal("exact", exact.GetProperty("files")[0].GetProperty("matchKind").GetString());
    }

    [Fact]
    public async Task AnalyzeStackTrace_MapsFramesToSource()
    {
        const string source = """
            namespace Probe;

            internal class Worker
            {
                public void Run() { }
            }
            """;

        var workspace = FakeWorkspace.Create(("Worker.cs", source));
        using var scope = workspace;
        var service = new WorkspaceExplorationService(workspace);

        var trace = """
               at Probe.Worker.Run() in D:\somewhere\Worker.cs:line 5
               at Probe.Worker.Missing() in D:\somewhere\Worker.cs:line 9
               at System.Runtime.CompilerServices.TaskAwaiter.GetResult()
            """;

        var result = ResultAssert.Json(await service.AnalyzeStackTraceAsync(trace, 10));

        Assert.Equal(2, result.GetProperty("frames").GetInt32());
        Assert.Equal(1, result.GetProperty("unresolved").GetInt32());
        var stack = result.GetProperty("stack").EnumerateArray().ToList();
        Assert.Equal(5, stack[0].GetProperty("line").GetInt32());
        Assert.Equal(9, stack[1].GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task ImpactAnalysis_ReportsWhenGitCannotAnswer()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var scope = workspace;
        var service = new TestWorkflowService(workspace);

        // The fake workspace is not a git repository: the tool must say so, not throw.
        var result = ResultAssert.Json(await service.ImpactAnalysisAsync(null, null, 50));
        Assert.True(result.TryGetProperty("error", out var error), result.GetRawText());
        Assert.Contains("git diff failed", error.GetString());
    }
}
