using Snitch.Languages.CSharp.Testing;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The suite runner's project selection and the record rerun_failed reads: a filter has to select a
/// project (not filter test names), and the record has to survive a run and be cleared by a green one.
/// </summary>
public sealed class TestRunSelectionAndRecordTests : IDisposable
{
    private const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        {0}
        </Project>
        """;

    private readonly FakeWorkspace _workspace;
    private readonly string _recordDirectory;

    public TestRunSelectionAndRecordTests()
    {
        _workspace = FakeWorkspace.CreateProjects(
            ("App", [("App.cs", "namespace App; internal class App { }")]),
            ("App.Tests", [("AppTests.cs", "namespace App.Tests; internal class AppTests { }")]),
            ("Other.IntegrationTests", [("OtherTests.cs", "namespace Other.IntegrationTests; internal class OtherTests { }")]));

        WriteProject("App.csproj", testSdk: false);
        WriteProject("App.Tests.csproj", testSdk: true);
        WriteProject("Other.IntegrationTests.csproj", testSdk: true);

        _recordDirectory = Path.Combine(Path.GetTempPath(), "vmcp-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_recordDirectory);
    }

    [Fact]
    public void The_project_filter_selects_test_projects_and_names_the_rest()
    {
        var solution = _workspace.CurrentSolution!;

        var all = RunTestsService.SelectTestProjects(solution, null, out var noError);
        Assert.Null(noError);
        Assert.Equal(["App.Tests", "Other.IntegrationTests"], all.Select(project => project.Name));

        var selected = RunTestsService.SelectTestProjects(solution, "integration", out var filterError);
        Assert.Null(filterError);
        Assert.Equal("Other.IntegrationTests", Assert.Single(selected).Name);

        // A file name selects as well as a project name does.
        Assert.Single(RunTestsService.SelectTestProjects(solution, "App.Tests.csproj", out _));

        var none = RunTestsService.SelectTestProjects(solution, "Platform", out var missingError);
        Assert.Empty(none);
        Assert.Contains("App.Tests", missingError);
        Assert.Contains("Other.IntegrationTests", missingError);
    }

    [Fact]
    public void A_project_without_the_test_sdk_is_not_a_test_project()
    {
        var detected = RunTestsService.DetectTestProjects(_workspace.CurrentSolution!);
        Assert.DoesNotContain("App", detected.Select(project => project.Name));
    }

    [Fact]
    public void The_record_round_trips_and_a_green_run_clears_it()
    {
        TestRunRecordStore.Write(_recordDirectory,
        [
            new FailedTestGroup("App.Tests", ["App.Tests.A", "App.Tests.B"]),
            new FailedTestGroup("Other.IntegrationTests", []),
        ]);

        var record = TestRunRecordStore.Read(_recordDirectory);
        Assert.NotNull(record);

        var group = Assert.Single(record!.Projects);
        Assert.Equal("App.Tests", group.Project);
        Assert.Equal(["App.Tests.A", "App.Tests.B"], group.Failed);
        Assert.Equal(2, record.AllFailures().Count());

        TestRunRecordStore.Write(_recordDirectory, []);
        var cleared = TestRunRecordStore.Read(_recordDirectory);
        Assert.NotNull(cleared);
        Assert.Empty(cleared!.Projects);
    }

    [Fact]
    public void A_record_from_the_first_shape_is_still_read()
    {
        var path = TestRunRecordStore.PathFor(_recordDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"Project":"App.Tests","Failed":["App.Tests.A"],"At":"2026-11-01T10:00:00+00:00"}
            """);

        var record = TestRunRecordStore.Read(_recordDirectory);
        Assert.NotNull(record);
        Assert.Equal("App.Tests", record!.Projects.Single().Project);
        Assert.Equal("App.Tests.A", record.AllFailures().Single().Test);
    }

    [Fact]
    public void A_recorded_failure_re_runs_exactly()
    {
        var filter = RunTestsService.ComposeFailureFilter(["App.Tests.A", "Divide"]);

        Assert.Equal("FullyQualifiedName=App.Tests.A|FullyQualifiedName~Divide", filter);
    }

    [Fact]
    public void The_command_targets_the_project_and_never_turns_it_into_a_test_filter()
    {
        var command = RunTestsService.BuildArgs("C:\\src\\App.Tests\\App.Tests.csproj", "C:\\tmp\\run-0", null);

        Assert.Contains("C:\\src\\App.Tests\\App.Tests.csproj", command);
        Assert.Contains("C:\\tmp\\run-0", command);
        Assert.DoesNotContain("FullyQualifiedName", command);
        Assert.Contains("--filter Name=x",
            RunTestsService.BuildArgs("app.dll", "out", "--filter Name=x"));
    }

    [Fact]
    public async Task Rerun_failed_says_when_nothing_is_recorded()
    {
        TestRunRecordStore.Write(_workspace.RootDirectory, []);
        var service = new TestWorkflowService(_workspace);

        var result = ResultAssert.Json(await service.RerunFailedAsync(null, 10));

        Assert.Contains("No failed tests are recorded", result.GetProperty("error").GetString());
    }

    private void WriteProject(string name, bool testSdk) =>
        File.WriteAllText(
            _workspace.PathOf(name),
            string.Format(ProjectFile, testSdk ? "      <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" />" : string.Empty));

    public void Dispose()
    {
        _workspace.Dispose();
        try { Directory.Delete(_recordDirectory, recursive: true); } catch { /* best effort */ }
    }
}
