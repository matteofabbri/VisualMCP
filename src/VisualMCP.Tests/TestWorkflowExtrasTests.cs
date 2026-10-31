using System.Text.Json;
using Microsoft.CodeAnalysis;
using Snitch.Debugging;
using Snitch.Languages.CSharp.Testing;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The test-workflow extras: discovery from the source, the flaky classification, the affected-tests
/// composition, the continuous loop's status and the VSTest host-pid line.
/// </summary>
public sealed class TestWorkflowExtrasTests
{
    // ── discovery ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Discovery_lists_facts_theories_with_rows_and_other_frameworks()
    {
        using var workspace = FakeWorkspace.Create(("Tests.cs", """
            using Xunit;
            using NUnit.Framework;
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            namespace Probe;

            public class SampleTests
            {
                [Fact]
                public void Adds() { }

                [Theory]
                [InlineData(1, 2)]
                [InlineData(3, 4)]
                public void AddsNumbers(int a, int b) { }

                [Test]
                [TestCase(1)]
                public void NUnitStyle(int value) { }

                [TestMethod]
                [DataRow(7)]
                public void MSTestStyle(int value) { }
            }
            """));

        var result = ResultAssert.Json(await new TestDiscoveryService(workspace).ListAsync());

        Assert.Equal(4, result.GetProperty("total").GetInt32());
        Assert.Equal(3, result.GetProperty("parameterized").GetInt32());

        var byFramework = result.GetProperty("byFramework");
        Assert.Equal(2, byFramework.GetProperty("xUnit").GetInt32());
        Assert.Equal(1, byFramework.GetProperty("NUnit").GetInt32());
        Assert.Equal(1, byFramework.GetProperty("MSTest").GetInt32());

        var theory = result.GetProperty("tests").EnumerateArray()
            .First(test => test.GetProperty("method").GetString() == "AddsNumbers");
        Assert.Equal(2, theory.GetProperty("dataRows").GetArrayLength());
        Assert.Equal("1, 2", theory.GetProperty("dataRows")[0].GetString());
    }

    [Fact]
    public async Task Discovery_filters_by_class_or_method_name()
    {
        using var workspace = FakeWorkspace.Create(("Tests.cs", """
            using Xunit;

            namespace Probe;

            public class AlphaTests { [Fact] public void One() { } }
            public class BetaTests { [Fact] public void Two() { } }
            """));

        var result = ResultAssert.Json(await new TestDiscoveryService(workspace).ListAsync(filter: "Beta"));

        Assert.Equal(1, result.GetProperty("total").GetInt32());
        Assert.Equal("BetaTests", result.GetProperty("tests")[0].GetProperty("class").GetString());
    }

    // ── flaky ───────────────────────────────────────────────────────────────
    [Fact]
    public void Flaky_classification_separates_flaky_from_always_failing_and_missing()
    {
        var outcomes = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Passes"] = ["Passed", "Passed", "Passed"],
            ["Flaky"] = ["Passed", "Failed", "Passed"],
            ["Broken"] = ["Failed", "Failed", "Failed"],
            ["Sometimes"] = ["Passed", "Skipped", "Passed"],
        };

        var classification = FlakyTestsService.Classify(outcomes, 3);

        Assert.Equal(["Flaky", "Sometimes"], classification.Flaky.Select(entry => entry.Test).OrderBy(name => name));
        Assert.Equal(["Broken"], classification.AlwaysFailing);
        Assert.Empty(classification.Unstable);
    }

    [Fact]
    public void A_test_missing_from_some_runs_is_not_flaky()
    {
        var outcomes = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Seen"] = ["Passed", "Passed"],
        };

        var classification = FlakyTestsService.Classify(outcomes, 3);

        Assert.Empty(classification.Flaky);
        Assert.Single(classification.Unstable);
        Assert.Equal(("Seen", 2), classification.Unstable[0]);
    }

    // ── filters and run summaries ───────────────────────────────────────────
    [Fact]
    public void The_filter_composes_from_a_test_name_a_class_or_raw_text()
    {
        Assert.Equal("FullyQualifiedName~Divide", TestSuiteRunner.ComposeFilter("Divide", null, null));
        Assert.Equal("FullyQualifiedName=Probe.Calculator.Divide", TestSuiteRunner.ComposeFilter("Probe.Calculator.Divide", null, null));
        Assert.Equal("FullyQualifiedName~CalculatorTests", TestSuiteRunner.ComposeFilter(null, "CalculatorTests", null));
        Assert.Equal("Name=Adds", TestSuiteRunner.ComposeFilter("Divide", "CalculatorTests", "Name=Adds"));
        Assert.Null(TestSuiteRunner.ComposeFilter(null, null, null));
    }

    [Fact]
    public void A_run_summary_counts_the_outcomes()
    {
        var result = new SuiteResult(
            new Dictionary<string, string>
            {
                ["A"] = "Passed",
                ["B"] = "Passed",
                ["C"] = "Failed",
                ["D"] = "NotExecuted",
            },
            Ran: true, Error: null, Output: string.Empty, ExitCode: 1, TimedOut: false);

        Assert.Equal(2, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Skipped);
    }

    // ── affected tests ──────────────────────────────────────────────────────
    [Fact]
    public async Task Affected_tests_run_exactly_the_composed_filter()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        var workflow = new StubWorkflow(affected: 2, filter: "FullyQualifiedName~Alpha|FullyQualifiedName~Beta");

        var result = ResultAssert.Json(
            await new AffectedTestsService(workspace, workflow).RunAsync("HEAD", null, null, 60));

        Assert.Equal("ran", result.GetProperty("verdict").GetString());
        Assert.Equal(2, result.GetProperty("affectedTests").GetInt32());
        Assert.Equal("FullyQualifiedName~Alpha|FullyQualifiedName~Beta", workflow.LastFilter);
    }

    [Fact]
    public async Task No_covering_test_is_its_own_verdict()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        var workflow = new StubWorkflow(affected: 0, filter: "");

        var result = ResultAssert.Json(
            await new AffectedTestsService(workspace, workflow).RunAsync("HEAD", null, null, 60));

        Assert.Equal("no-affected-tests", result.GetProperty("verdict").GetString());
        Assert.Null(workflow.LastFilter);
    }

    // ── continuous testing ──────────────────────────────────────────────────
    [Fact]
    public async Task The_continuous_loop_starts_reports_and_stops()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        var service = new ContinuousTestingService(workspace);

        var started = ResultAssert.Json(await service.StartAsync(2, null, null));
        Assert.True(started.GetProperty("running").GetBoolean());

        var status = ResultAssert.Json(service.Status());
        Assert.True(status.GetProperty("running").GetBoolean());
        Assert.Equal(2, status.GetProperty("intervalSeconds").GetInt32());

        var stopped = ResultAssert.Json(await service.StopAsync());
        Assert.False(stopped.GetProperty("running").GetBoolean());

        var after = ResultAssert.Json(service.Status());
        Assert.False(after.GetProperty("running").GetBoolean());
    }

    // ── debug a test ────────────────────────────────────────────────────────
    [Fact]
    public void The_vstest_host_pid_line_is_recognised()
    {
        Assert.True(DebugTestService.TryParseHostProcessId("Process Id: 12345, Name: testhost", out var pid));
        Assert.Equal(12345, pid);

        Assert.True(DebugTestService.TryParseHostProcessId("  process id: 7  ", out pid));
        Assert.Equal(7, pid);

        Assert.False(DebugTestService.TryParseHostProcessId("Waiting for debugger attach...", out _));
        Assert.False(DebugTestService.TryParseHostProcessId("Process Id: none", out _));
    }

    private sealed class StubWorkflow : ITestWorkflowService
    {
        private readonly int _affected;
        private readonly string _filter;

        public StubWorkflow(int affected, string filter)
        {
            _affected = affected;
            _filter = filter;
        }

        public string? LastFilter { get; private set; }

        public Task<object> ImpactAnalysisAsync(string? baseRef, string? projectName, int maxResults) =>
            Task.FromResult<object>(new
            {
                baseRef = "HEAD",
                changedFiles = 3,
                count = _affected,
                filter = _filter,
            });

        public Task<object> RunTestAsync(string? testName, string? className, string? projectName, string? filter, int timeoutSeconds)
        {
            LastFilter = filter;
            return Task.FromResult<object>(new { succeeded = true, passed = 2, failed = 0, filter });
        }

        public Task<object> RerunFailedAsync(string? projectName, int timeoutSeconds) => throw new NotSupportedException();
        public Task<object> TestsCoveringSymbolAsync(string symbolName, string? containingType, int maxResults) => throw new NotSupportedException();
        public Task<object> GenerateTestsAsync(string typeName, string? projectName, string? testProjectName, string? className, bool dryRun) => throw new NotSupportedException();
    }
}
