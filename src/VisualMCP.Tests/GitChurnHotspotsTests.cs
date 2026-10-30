using Snitch.Git;
using Snitch.Languages.CSharp.Analysis;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// git_churn_hotspots answers one question the two signals cannot answer alone: a file that changes
/// every week and is hard to read is where a refactoring pays first; a huge file nobody touches is not.
/// These tests build a real repository inside the fake solution's folder, so the git side is the real
/// CLI and the complexity side is the real Roslyn ranking.
/// </summary>
public sealed class GitChurnHotspotsTests
{
    private static async Task Git(string repo, params string[] args)
    {
        var (exitCode, timedOut, _, stderr, _) = await GitCli.RunAsync(repo, string.Join(' ', args), 30);
        Assert.True(exitCode == 0 && !timedOut, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static string HotSource(int version) => $$"""
        public class Hot
        {
            // revision {{version}}
            public int Score(int x)
            {
                if (x > 0)
                {
                    if (x > 10) return 3;
                    return 2;
                }

                return 1;
            }
        }
        """;

    private static GitChurnHotspotsService Service(FakeWorkspace workspace) =>
        new(workspace, new ComplexityReportService(workspace));

    [Fact]
    public async Task The_file_that_changes_most_and_is_complex_ranks_first()
    {
        using var workspace = FakeWorkspace.Create(
            ("Hot.cs", HotSource(0)),
            ("Cold.cs", "public class Cold { public int Value => 1; }"));

        var repo = workspace.RootDirectory;
        await Git(repo, "init");
        await Git(repo, "config", "user.email", "test@example.com");
        await Git(repo, "config", "user.name", "Test");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-m", "initial");

        for (var revision = 1; revision <= 3; revision++)
        {
            File.WriteAllText(workspace.PathOf("Hot.cs"), HotSource(revision));
            await Git(repo, "add", "-A");
            await Git(repo, "commit", "-m", $"change{revision}");
        }

        var result = await Service(workspace).AnalyzeAsync(
            repoPath: repo, since: "10 years ago", top: 10, minCommits: 2);
        ResultAssert.Succeeded(result);

        var hotspots = ResultAssert.Json(result).GetProperty("hotspots").EnumerateArray().ToList();
        Assert.NotEmpty(hotspots);

        var first = hotspots[0];
        Assert.EndsWith("Hot.cs", first.GetProperty("file").GetString());
        Assert.True(first.GetProperty("commits").GetInt32() >= 3);
        Assert.True(first.GetProperty("maxCyclomatic").GetInt32() >= 3);
        Assert.True(first.GetProperty("score").GetDouble() > 0);
    }

    [Fact]
    public async Task A_file_below_the_commit_floor_is_not_a_hotspot()
    {
        using var workspace = FakeWorkspace.Create(
            ("Hot.cs", HotSource(0)),
            ("Cold.cs", "public class Cold { public int Value => 1; }"));

        var repo = workspace.RootDirectory;
        await Git(repo, "init");
        await Git(repo, "config", "user.email", "test@example.com");
        await Git(repo, "config", "user.name", "Test");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-m", "initial");

        for (var revision = 1; revision <= 3; revision++)
        {
            File.WriteAllText(workspace.PathOf("Hot.cs"), HotSource(revision));
            await Git(repo, "add", "-A");
            await Git(repo, "commit", "-m", $"change{revision}");
        }

        var result = await Service(workspace).AnalyzeAsync(
            repoPath: repo, since: "10 years ago", top: 10, minCommits: 2);
        ResultAssert.Succeeded(result);

        var files = ResultAssert.Json(result).GetProperty("hotspots").EnumerateArray()
            .Select(entry => entry.GetProperty("file").GetString())
            .ToList();

        Assert.DoesNotContain(files, file => file!.EndsWith("Cold.cs"));
    }

    [Fact]
    public async Task A_repository_without_commits_is_an_empty_answer_not_an_exception()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "public class Probe { }"));

        var repo = workspace.RootDirectory;
        await Git(repo, "init");

        var result = await Service(workspace).AnalyzeAsync(repoPath: repo, since: "10 years ago");
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("commitsScanned").GetInt32());
        Assert.Empty(json.GetProperty("top").EnumerateArray());
    }
}
