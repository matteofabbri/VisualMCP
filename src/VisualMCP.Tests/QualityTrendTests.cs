using Snitch.Git;
using Snitch.Languages.CSharp.Analysis.QualityTrend;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The quality trend: the syntactic reader that makes a revision measurable without a compilation, the
/// comparer that turns two sides into a verdict, and the git plumbing that reads a revision as source.
/// The metric rules are tested directly so a failure names the rule that broke, not 'the tool'.
/// </summary>
public sealed class QualityTrendTests
{
    private static async Task Git(string repo, params string[] args)
    {
        var (exitCode, timedOut, _, stderr, _) = await GitCli.RunAsync(repo, string.Join(' ', args), 30);
        Assert.True(exitCode == 0 && !timedOut, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static RevisionQuality Revision(string name, params (string Path, string Source)[] files) =>
        new(name, name, files.ToDictionary(file => file.Path, file => SourceQualityReader.Read(file.Path, file.Source)));

    [Fact]
    public void Every_decision_point_adds_one_path_to_the_method()
    {
        var quality = SourceQualityReader.Read("Hot.cs", """
            public class Hot
            {
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
            """);

        var method = Assert.Single(quality.Methods);
        Assert.Equal("Hot.Score", method.Name);
        Assert.Equal(3, method.Complexity);
        Assert.Equal(1, quality.Types);
    }

    [Fact]
    public void A_local_function_is_its_own_method_and_is_not_counted_twice()
    {
        var quality = SourceQualityReader.Read("Probe.cs", """
            public class Probe
            {
                public int Run(int x)
                {
                    int Twice(int value) => value > 0 ? value * 2 : -value;
                    return Twice(x);
                }
            }
            """);

        Assert.Equal(2, quality.Methods.Count);
        Assert.Equal(1, quality.Methods.Single(method => method.Name == "Probe.Run").Complexity);
        Assert.Equal(2, quality.Methods.Single(method => method.Name == "Probe.Twice").Complexity);
    }

    [Fact]
    public void A_body_over_forty_lines_is_long_and_todo_markers_are_counted()
    {
        var statements = string.Join("\n", Enumerable.Range(0, 42).Select(index => $"        var value{index} = {index};"));

        var quality = SourceQualityReader.Read("Long.cs", $$"""
            public class Long
            {
                // TODO: split this
                public int Work()
                {
            {{statements}}
                    return 0;
                }
            }
            """);

        Assert.Equal(1, quality.LongMethods);
        Assert.Equal(1, quality.Todos);
    }

    [Fact]
    public void Generated_files_are_recognised()
    {
        Assert.True(SourceQualityReader.IsGenerated("obj/Debug/Probe.g.cs"));
        Assert.True(SourceQualityReader.IsGenerated("Probe.Designer.cs"));
        Assert.False(SourceQualityReader.IsGenerated("Probe.cs"));
    }

    [Fact]
    public void A_method_that_gained_decisions_is_a_regression()
    {
        var before = Revision("before", ("A.cs", """
            public class A
            {
                public int Get(int x) => x;
            }
            """));

        var after = Revision("after", ("A.cs", """
            public class A
            {
                public int Get(int x)
                {
                    if (x > 0)
                    {
                        if (x > 1) return 1;
                        return 2;
                    }

                    return 0;
                }
            }
            """));

        var diff = QualityTrendComparer.Compare(before, after, 5);

        Assert.Equal("regressed", diff.Verdict);

        var average = diff.Signals.Single(signal => signal.Metric == "averageComplexity");
        Assert.Equal("worse", average.Direction);
        Assert.Equal(1, average.Before);
        Assert.Equal(3, average.After);
    }

    [Fact]
    public void Removing_decisions_is_an_improvement()
    {
        var simpler = """
            public class A
            {
                public int Get(int x) => x;
            }
            """;

        var complex = """
            public class A
            {
                public int Get(int x)
                {
                    if (x > 0)
                    {
                        if (x > 1) return 1;
                        return 2;
                    }

                    return 0;
                }
            }
            """;

        var diff = QualityTrendComparer.Compare(
            Revision("before", ("A.cs", complex)),
            Revision("after", ("A.cs", simpler)),
            5);

        Assert.Equal("improved", diff.Verdict);
        Assert.Single(diff.BetterFiles);
        Assert.Empty(diff.WorseFiles);
    }

    [Fact]
    public void Adding_a_simple_method_is_context_not_a_regression()
    {
        var one = Revision("one", ("A.cs", "public class A { public int Get(int x) => x; }"));

        var two = Revision("two", ("A.cs", """
            public class A
            {
                public int Get(int x) => x;
                public int Other(int y) => y;
            }
            """));

        var diff = QualityTrendComparer.Compare(one, two, 5);

        Assert.Equal("unchanged", diff.Verdict);
        Assert.Equal("neutral", diff.Signals.Single(signal => signal.Metric == "complexity").Direction);
        Assert.Equal(2, diff.Signals.Single(signal => signal.Metric == "methods").After);
    }

    [Fact]
    public async Task The_trend_between_two_commits_names_the_file_that_got_worse()
    {
        using var workspace = FakeWorkspace.Create(
            ("Hard.cs", "public class Hard { public int Score(int x) => x; }"),
            ("Probe.cs", "public class Probe { }"));

        var repo = workspace.RootDirectory;
        await Git(repo, "init");
        await Git(repo, "config", "user.email", "tests@example.com");
        await Git(repo, "config", "user.name", "VisualMCP Tests");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-m", "baseline");

        File.WriteAllText(workspace.PathOf("Hard.cs"), """
            public class Hard
            {
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
            """);

        File.WriteAllText(workspace.PathOf("Fresh.cs"), "public class Fresh { public int Get(int x) => x; }");

        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-m", "harder");

        var result = await new GetQualityTrendService(workspace)
            .MeasureAsync(fromRef: "HEAD~1", toRef: "HEAD", repoPath: repo, path: null, top: 5);

        ResultAssert.Succeeded(result);
        var json = ResultAssert.Json(result);

        Assert.Equal("regressed", json.GetProperty("verdict").GetString());
        Assert.True(json.GetProperty("from").GetProperty("metrics").GetProperty("complexity").GetInt32() <
                    json.GetProperty("to").GetProperty("metrics").GetProperty("complexity").GetInt32());

        var worse = Assert.Single(json.GetProperty("worseFiles").EnumerateArray());
        Assert.EndsWith("Hard.cs", worse.GetProperty("File").GetString());
        Assert.Equal(2, worse.GetProperty("Delta").GetInt32());

        Assert.Contains(json.GetProperty("addedFiles").EnumerateArray(),
            file => file.GetString()!.EndsWith("Fresh.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_working_tree_can_be_the_second_side()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "public class Probe { public int Get(int x) => x; }"));

        var repo = workspace.RootDirectory;
        await Git(repo, "init");
        await Git(repo, "config", "user.email", "tests@example.com");
        await Git(repo, "config", "user.name", "VisualMCP Tests");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-m", "baseline");

        File.WriteAllText(workspace.PathOf("Probe.cs"), """
            public class Probe
            {
                public int Get(int x)
                {
                    if (x > 0 && x < 10) return 1;
                    return 0;
                }
            }
            """);

        var result = await new GetQualityTrendService(workspace)
            .MeasureAsync(fromRef: "HEAD", toRef: "worktree", repoPath: repo, path: null, top: 5);

        ResultAssert.Succeeded(result);
        var json = ResultAssert.Json(result);

        Assert.True(json.GetProperty("to").GetProperty("worktree").GetBoolean());
        Assert.Equal("regressed", json.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Comparing_a_revision_with_itself_is_a_clear_refusal()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "public class Probe { }"));

        var repo = workspace.RootDirectory;
        await Git(repo, "init");
        await Git(repo, "config", "user.email", "tests@example.com");
        await Git(repo, "config", "user.name", "VisualMCP Tests");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-m", "baseline");

        var result = await new GetQualityTrendService(workspace)
            .MeasureAsync(fromRef: "HEAD", toRef: "HEAD", repoPath: repo, path: null, top: 5);

        Assert.Contains("same revision", ResultAssert.Error(result));
    }
}
