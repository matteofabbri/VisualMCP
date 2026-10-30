using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Snitch;
using Snitch.Execution;
using VisualMCP.Execution;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The plan executor: concurrency, dependency order, the path rule that keeps parallel edits apart,
/// skip-on-failure, for-each expansion, and the validation that refuses a plan before anything runs.
/// </summary>
public sealed class PlanExecutionTests
{
    [Fact]
    public async Task IndependentStepsRunConcurrently()
    {
        var tools = new FakeTools { Delay = TimeSpan.FromMilliseconds(150) };
        var plan = Plan([Tool("a"), Tool("b"), Tool("c"), Tool("d")], maxConcurrency: 4);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal("ok", report.GetProperty("verdict").GetString());
        Assert.Equal(4, report.GetProperty("counts").GetProperty("ok").GetInt32());
        Assert.True(tools.Peak >= 3, $"the steps never overlapped (peak {tools.Peak}).");
        Assert.True(report.GetProperty("concurrency").GetProperty("peak").GetInt32() >= 3);
        Assert.True(report.GetProperty("concurrency").GetProperty("savedMs").GetInt64() > 0);
    }

    [Fact]
    public async Task AStepStartsOnlyAfterItsDependencyFinished()
    {
        var tools = new FakeTools { Known = ["a", "b"], Delay = TimeSpan.FromMilliseconds(60) };
        var plan = Plan([Tool("a"), Tool("b", dependsOn: ["a"])]);

        await Run(plan, tools);

        var order = tools.Started.ToArray();
        Assert.Equal(["a", "b"], order);
    }

    [Fact]
    public async Task StepsThatDeclareTheSamePathNeverOverlap()
    {
        var tools = new FakeTools { Delay = TimeSpan.FromMilliseconds(80) };
        var plan = Plan(
            [
                Tool("a", paths: ["src/One.cs"]),
                Tool("b", paths: ["src/One.cs"]),
                Tool("c", paths: ["src/One.cs"]),
            ],
            maxConcurrency: 3);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal(1, tools.Peak);
        Assert.True(report.GetProperty("concurrency").GetProperty("serializedByPaths").GetInt32() > 0);
    }

    [Fact]
    public async Task StepsOnDisjointPathsDoOverlap()
    {
        var tools = new FakeTools { Delay = TimeSpan.FromMilliseconds(80) };
        var plan = Plan(
            [
                Tool("a", paths: ["src/One.cs"]),
                Tool("b", paths: ["src/Two.cs"]),
                Tool("c", paths: ["src/Three.cs"]),
            ],
            maxConcurrency: 3);

        await Run(plan, tools);

        Assert.True(tools.Peak >= 2, $"disjoint steps were serialised anyway (peak {tools.Peak}).");
    }

    [Fact]
    public async Task AFailedStepSkipsTheStepsThatDependedOnIt()
    {
        var tools = new FakeTools
        {
            Known = ["a", "b", "c"],
            Handler = (name, _) => Task.FromResult<object?>(name == "a" ? new { error = "boom" } : new { ok = true }),
        };
        var plan = Plan([Tool("a"), Tool("b", dependsOn: ["a"]), Tool("c")]);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal("failed", report.GetProperty("verdict").GetString());
        Assert.Equal("skipped", Step(report, "b").GetProperty("status").GetString());
        Assert.Equal("failed", Step(report, "a").GetProperty("status").GetString());
        Assert.Equal("boom", Step(report, "a").GetProperty("error").GetString());
        Assert.DoesNotContain("b", tools.Started);
    }

    [Fact]
    public async Task ContinueOnErrorLetsTheDependentsRun()
    {
        var tools = new FakeTools
        {
            Known = ["a", "b"],
            Handler = (name, _) => Task.FromResult<object?>(name == "a" ? new { error = "boom" } : new { ok = true }),
        };
        var plan = Plan([Tool("a", continueOnError: true), Tool("b", dependsOn: ["a"])]);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal("partial", report.GetProperty("verdict").GetString());
        Assert.Equal("ok", Step(report, "b").GetProperty("status").GetString());
    }

    [Fact]
    public async Task OnFailureContinueRunsEveryUnrelatedStep()
    {
        var tools = new FakeTools
        {
            Known = ["a", "b"],
            Handler = (name, _) => Task.FromResult<object?>(name == "a" ? new { error = "boom" } : new { ok = true }),
        };
        var plan = Plan([Tool("a"), Tool("b")], onFailure: "continue", maxConcurrency: 1);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal("ok", Step(report, "b").GetProperty("status").GetString());
        Assert.Equal("failed", report.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task ForEachExpandsOneStepPerItemAndSubstitutesThePlaceholder()
    {
        var seen = new ConcurrentBag<string>();
        var tools = new FakeTools
        {
            Handler = (_, arguments) =>
            {
                seen.Add(arguments!.Value.GetProperty("file").GetString()!);
                return Task.FromResult<object?>(new { ok = true });
            },
        };
        var plan = Plan([Tool("sweep", forEach: ["A.cs", "B.cs"], arguments: Arguments("""{"file":"${item}"}"""))]);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal(["A.cs", "B.cs"], seen.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, report.GetProperty("counts").GetProperty("ok").GetInt32());
        var ids = report.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();
        Assert.Contains("sweep[0]", ids);
        Assert.Contains("sweep[1]", ids);
    }

    [Fact]
    public async Task ADependantOfAFanOutWaitsForEveryInstance()
    {
        var tools = new FakeTools { Known = ["sweep", "after"], Delay = TimeSpan.FromMilliseconds(40) };
        var plan = Plan(
            [
                Tool("sweep", forEach: ["A", "B", "C"]),
                Tool("after", dependsOn: ["sweep"]),
            ],
            maxConcurrency: 4);

        await Run(plan, tools);

        Assert.Equal(4, tools.Started.Count);
        Assert.Equal("after", tools.Started.Last());
    }

    [Fact]
    public async Task CommandStepsReportTheirExitCode()
    {
        var commands = new FakeCommands
        {
            Handler = command => command == "fail"
                ? new { exitCode = 1, timedOut = false, stdout = "", stderr = "bad" }
                : new { exitCode = 0, timedOut = false, stdout = "done", stderr = "" },
        };
        var plan = Plan([Command("ok", "good"), Command("bad", "fail")], onFailure: "continue");

        var report = ResultAssert.Json(await Run(plan, commands, new FakeTools()));

        Assert.Equal("ok", Step(report, "ok").GetProperty("status").GetString());
        Assert.Equal("failed", Step(report, "bad").GetProperty("status").GetString());
        Assert.Equal(1, Step(report, "bad").GetProperty("exitCode").GetInt32());
        Assert.Equal("done", Step(report, "ok").GetProperty("result").GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task ALongResultIsTruncatedToTheStepBudget()
    {
        var tools = new FakeTools { Handler = (_, _) => Task.FromResult<object?>(new { big = new string('x', 5000) }) };
        var plan = Plan([Tool("a")], outputChars: 200);

        var report = ResultAssert.Json(await Run(plan, tools));
        var step = Step(report, "a");

        Assert.True(step.GetProperty("truncated").GetBoolean());
        Assert.Equal(200, step.GetProperty("result").GetProperty("preview").GetString()!.Length);
    }

    // ── validation: the plan is refused before anything runs ─────────────────
    [Fact]
    public async Task ACycleIsRejectedAndNothingRuns()
    {
        var tools = new FakeTools { Known = ["a", "b"] };
        var plan = Plan([Tool("a", dependsOn: ["b"]), Tool("b", dependsOn: ["a"])]);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal("rejected", report.GetProperty("verdict").GetString());
        Assert.Contains(report.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("cycle"));
        Assert.Empty(tools.Started);
    }

    [Fact]
    public async Task AnUnknownToolIsRejectedAndNothingRuns()
    {
        var tools = new FakeTools();
        var plan = Plan([Tool("a"), Tool("b", tool: "no_such_tool")]);

        var report = ResultAssert.Json(await Run(plan, tools));

        Assert.Equal("rejected", report.GetProperty("verdict").GetString());
        Assert.Contains(report.GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.Contains("no_such_tool"));
        Assert.Empty(tools.Started);
    }

    [Fact]
    public async Task AnUnknownDependencyIsRejected()
    {
        var report = ResultAssert.Json(await Run(Plan([Tool("a", dependsOn: ["ghost"])]), new FakeTools()));

        Assert.Equal("rejected", report.GetProperty("verdict").GetString());
        Assert.Contains(report.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("ghost"));
    }

    [Fact]
    public async Task ADuplicateIdIsRejected()
    {
        var report = ResultAssert.Json(await Run(Plan([Tool("same"), Tool("same")]), new FakeTools()));

        Assert.Equal("rejected", report.GetProperty("verdict").GetString());
        Assert.Contains(report.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("more than once"));
    }

    [Fact]
    public async Task AnEmptyPlanIsRejected()
    {
        var report = ResultAssert.Json(await Run(new PlanSpec(), new FakeTools()));

        Assert.Equal("rejected", report.GetProperty("verdict").GetString());
    }

    // ── the real invoker: reflection over this server's own tools ────────────
    [Fact]
    public void TheRealInvokerSeesTheWholeCatalogue()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var invoker = new McpToolInvoker(provider);

        Assert.Contains("run_plan", invoker.Names);
        Assert.Contains("simplify_code", invoker.Names);
        Assert.Contains("get_diagnostics", invoker.Names);      // snake_case alias of GetDiagnostics
        Assert.Contains("ListAnalysisTools", invoker.Names);    // canonical name, no attribute name
        Assert.True(invoker.Names.Count > 150, $"only {invoker.Names.Count} tools were discovered.");
    }

    [Fact]
    public async Task TheRealInvokerCallsAToolThatNeedsNoServices()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var invoker = new McpToolInvoker(provider);

        var result = await invoker.InvokeAsync("list_analysis_tools", null, CancellationToken.None);

        Assert.NotNull(result);
        var json = JsonSerializer.SerializeToElement(result);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
    }

    [Fact]
    public async Task TheRealInvokerRefusesTheOrchestratorItself()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var invoker = new McpToolInvoker(provider);

        var refused = await Assert.ThrowsAsync<PlanStepException>(
            () => invoker.InvokeAsync("run_plan", null, CancellationToken.None));

        Assert.Contains("may not call", refused.Message);
    }

    [Fact]
    public async Task TheWholeChainRunsARealCommandAndARealTool()
    {
        if (!OperatingSystem.IsWindows())
            return; // the command step is PowerShell; the tool half is platform-neutral.

        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var runner = new PlanCommandRunner(new RunCommandService(workspace, new SnitchOptions()));
        var invoker = new McpToolInvoker(new ServiceCollection().BuildServiceProvider());
        var plan = Plan(
            [
                Tool("catalogue", tool: "list_analysis_tools"),
                Command("sum", "2 + 2"),
                Tool("after", tool: "list_analysis_tools", dependsOn: ["catalogue", "sum"]),
            ],
            maxConcurrency: 2,
            outputChars: 500);

        var report = ResultAssert.Json(
            await new PlanExecutionService(runner, invoker, workspace).RunAsync(plan));

        Assert.Equal("ok", report.GetProperty("verdict").GetString());
        Assert.Equal(3, report.GetProperty("counts").GetProperty("ok").GetInt32());
        Assert.Equal("ok", Step(report, "sum").GetProperty("status").GetString());
        Assert.Contains("4", Step(report, "sum").GetProperty("result").GetProperty("stdout").GetString());
    }

    [Fact]
    public void PlanSpecBindsFromCamelCaseJson()
    {
        const string json = """
            { "name": "n", "maxConcurrency": 2, "onFailure": "continue",
              "steps": [ { "id": "a", "kind": "tool", "tool": "fake", "arguments": { "x": 1 },
                           "dependsOn": [], "paths": ["p"], "forEach": ["i"], "as": "f",
                           "timeoutSeconds": 5, "continueOnError": true } ] }
            """;

        var spec = JsonSerializer.Deserialize<PlanSpec>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var step = spec.Steps![0];

        Assert.Equal("n", spec.Name);
        Assert.Equal(2, spec.MaxConcurrency);
        Assert.Equal("continue", spec.OnFailure);
        Assert.Equal("a", step.Id);
        Assert.Equal("tool", step.Kind);
        Assert.Equal("fake", step.Tool);
        Assert.Equal(1, step.Arguments!.Value.GetProperty("x").GetInt32());
        Assert.Equal(["i"], step.ForEach!);
        Assert.Equal("f", step.As);
        Assert.Equal(5, step.TimeoutSeconds);
        Assert.True(step.ContinueOnError);
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static Task<object> Run(PlanSpec plan, FakeTools tools) => Run(plan, new FakeCommands(), tools);

    private static Task<object> Run(PlanSpec plan, FakeCommands commands, FakeTools tools) =>
        new PlanExecutionService(commands, tools).RunAsync(plan);

    private static PlanSpec Plan(
        List<PlanStepSpec> steps, int? maxConcurrency = null, string? onFailure = null, int? outputChars = null) => new()
    {
        Steps = steps,
        MaxConcurrency = maxConcurrency,
        OnFailure = onFailure,
        OutputChars = outputChars,
    };

    private static PlanStepSpec Tool(
        string id,
        string? tool = null,
        string[]? dependsOn = null,
        string[]? paths = null,
        JsonElement? arguments = null,
        bool? continueOnError = null,
        string[]? forEach = null) => new()
    {
        Id = id,
        Kind = "tool",
        Tool = tool ?? id,
        DependsOn = dependsOn,
        Paths = paths,
        Arguments = arguments,
        ContinueOnError = continueOnError,
        ForEach = forEach,
    };

    private static PlanStepSpec Command(string id, string command, string[]? dependsOn = null) => new()
    {
        Id = id,
        Kind = "command",
        Command = command,
        DependsOn = dependsOn,
    };

    private static JsonElement Arguments(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement Step(JsonElement report, string id) =>
        report.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == id);

    private sealed class FakeTools : IPlanToolInvoker
    {
        private int _running;
        private int _peak;

        public TimeSpan Delay { get; init; }
        public Func<string, JsonElement?, Task<object?>>? Handler { get; init; }
        public string[] Known { get; init; } = ["fake", "a", "b", "c", "d", "sweep", "after"];
        public ConcurrentQueue<string> Started { get; } = new();
        public int Peak => Volatile.Read(ref _peak);

        public IReadOnlyCollection<string> Names => Known;

        public async Task<object?> InvokeAsync(string name, JsonElement? arguments, CancellationToken ct)
        {
            Started.Enqueue(name);
            NotePeak(Interlocked.Increment(ref _running));
            try
            {
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, ct);

                return Handler is null ? new { ok = true } : await Handler(name, arguments);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private void NotePeak(int running)
        {
            var current = Volatile.Read(ref _peak);
            while (running > current)
            {
                var seen = Interlocked.CompareExchange(ref _peak, running, current);
                if (seen == current)
                    return;

                current = seen;
            }
        }
    }

    private sealed class FakeCommands : IPlanCommandRunner
    {
        public Func<string, object>? Handler { get; init; }

        public Task<object> RunAsync(
            string command, string shell, string? workingDirectory, int timeoutSeconds, CancellationToken ct) =>
            Task.FromResult(Handler?.Invoke(command)
                            ?? new { exitCode = 0, timedOut = false, stdout = "done", stderr = "" });
    }
}
