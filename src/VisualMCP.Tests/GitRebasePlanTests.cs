using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The interactive rebase as a plan: the same history rewrite a todo list gives, written by the
/// caller instead of typed into an editor.
/// </summary>
public sealed class GitRebasePlanTests
{
    private static (TestGitRepo Repo, string Root, string C1, string C2, string C3, string C4, string C5) History()
    {
        var repo = new TestGitRepo();
        repo.Write("base.txt", "root");
        var root = repo.Commit("root");

        // One file per commit: a rebase replays diffs, and a commit that rewrites the whole file the
        // previous one wrote would conflict for reasons that have nothing to do with the plan.
        repo.Write("a.txt", "c1");
        var c1 = repo.Commit("first");
        repo.Write("b.txt", "c2");
        var c2 = repo.Commit("second");
        repo.Write("c.txt", "c3");
        var c3 = repo.Commit("third");
        repo.Write("d.txt", "c4");
        var c4 = repo.Commit("fourth");
        repo.Write("e.txt", "c5");
        var c5 = repo.Commit("fifth");

        return (repo, root, c1, c2, c3, c4, c5);
    }

    [Fact]
    public async Task Plan_Rewords_Drops_And_SquashesInOneRun()
    {
        var (repo, root, c1, c2, c3, c4, c5) = History();
        using var _ = repo;

        var plan = $@"[
            {{ ""commit"": ""{c1}"", ""action"": ""pick"" }},
            {{ ""commit"": ""{c2}"", ""action"": ""drop"" }},
            {{ ""commit"": ""{c3}"", ""action"": ""reword"", ""message"": ""third, reworded"" }},
            {{ ""commit"": ""{c4}"", ""action"": ""pick"" }},
            {{ ""commit"": ""{c5}"", ""action"": ""squash"", ""message"": ""fourth and fifth, folded"" }}
        ]";

        var result = await repo.Service.RebaseAsync(root, "plan", plan, repo.Directory);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("rebased").GetBoolean());
        Assert.Equal(1, json.GetProperty("dropped").GetInt32());

        var subjects = repo.Capture("log", "--pretty=%s")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();

        Assert.Equal(new[] { "fourth and fifth, folded", "third, reworded", "first", "root" }, subjects);

        // The dropped commit's file is gone; the squashed commit keeps the last tree.
        Assert.False(File.Exists(Path.Combine(repo.Directory, "b.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Directory, "e.txt")));
        Assert.Equal("4", repo.Capture("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task Plan_PicksWithoutMessage_KeepTheirSubjects()
    {
        var (repo, root, c1, _, c3, _, _) = History();
        using var _1 = repo;

        var plan = $@"[
            {{ ""commit"": ""{c1}"", ""action"": ""pick"" }},
            {{ ""commit"": ""{c3}"", ""action"": ""pick"" }}
        ]";

        ResultAssert.Succeeded(await repo.Service.RebaseAsync(root, "plan", plan, repo.Directory));

        var subjects = repo.Capture("log", "--pretty=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToList();
        Assert.Equal(new[] { "third", "first", "root" }, subjects);
    }

    [Fact]
    public async Task Plan_UnknownAction_IsRefusedBeforeAnythingRuns()
    {
        var (repo, root, c1, _, _, _, _) = History();
        using var _1 = repo;

        var plan = $@"[{{ ""commit"": ""{c1}"", ""action"": ""explode"" }}]";
        var error = ResultAssert.Error(await repo.Service.RebaseAsync(root, "plan", plan, repo.Directory));

        Assert.Contains("unknown action", error);
        Assert.Equal("6", repo.Capture("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task Plan_RewordWithoutMessage_IsRefused()
    {
        var (repo, root, c1, _, _, _, _) = History();
        using var _1 = repo;

        var plan = $@"[{{ ""commit"": ""{c1}"", ""action"": ""reword"" }}]";
        var error = ResultAssert.Error(await repo.Service.RebaseAsync(root, "plan", plan, repo.Directory));

        Assert.Contains("needs a message", error);
    }

    [Fact]
    public async Task Plan_InvalidJson_IsRefused()
    {
        var (repo, root, _, _, _, _, _) = History();
        using var _1 = repo;

        var error = ResultAssert.Error(await repo.Service.RebaseAsync(root, "plan", "not json", repo.Directory));

        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public async Task Plan_RequiresABaseRevision()
    {
        var (repo, _, c1, _, _, _, _) = History();
        using var _1 = repo;

        var plan = $@"[{{ ""commit"": ""{c1}"", ""action"": ""pick"" }}]";
        var error = ResultAssert.Error(await repo.Service.RebaseAsync(null, "plan", plan, repo.Directory));

        Assert.Contains("base revision", error);
    }
}
