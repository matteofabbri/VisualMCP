using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The conflict model against a real merge produced by git: a merge editor needs the hunks as data,
/// so these tests check the parse, the resolution and the staging that follows.
/// </summary>
public sealed class GitConflictTests
{
    private const string Base = "line1\nshared\nline3\n";

    private static void MakeConflict(TestGitRepo repo, bool diff3 = false)
    {
        if (diff3) repo.Run("config", "merge.conflictStyle", "diff3");

        repo.Write("a.txt", Base);
        repo.Commit("base");

        repo.Run("checkout", "-q", "-b", "topic");
        repo.Write("a.txt", "line1\ntopic\nline3\n");
        repo.Commit("topic");

        repo.Run("checkout", "-q", "main");
        repo.Write("a.txt", "line1\nmain\nline3\n");
        repo.Commit("main on main");

        var merge = repo.Run("merge", "topic");
        Assert.NotEqual(0, merge.exitCode);
    }

    [Fact]
    public async Task List_ReportsTheOperationAndTheHunks()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo);

        var list = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "list", null, "ours", null, null, stage: true, repo.Directory));

        Assert.Equal("merge", list.GetProperty("operation").GetString());
        Assert.Equal(1, list.GetProperty("conflictCount").GetInt32());

        var file = list.GetProperty("files")[0];
        Assert.Equal("a.txt", file.GetProperty("path").GetString());
        Assert.Equal(1, file.GetProperty("hunks").GetInt32());
        Assert.False(file.GetProperty("binary").GetBoolean());

        var preview = file.GetProperty("preview")[0];
        Assert.Equal("main", preview.GetProperty("oursFirstLine").GetString());
        Assert.Equal("topic", preview.GetProperty("theirsFirstLine").GetString());
    }

    [Fact]
    public async Task List_WhenNothingIsConflicted_SaysSo()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "clean\n");
        repo.Commit("only commit");

        var list = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "list", null, "ours", null, null, stage: true, repo.Directory));

        Assert.Equal(0, list.GetProperty("conflictCount").GetInt32());
        Assert.Null(list.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task Show_ReturnsBothSidesAndTheDiff3Text()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo);

        var show = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "show", "a.txt", "ours", null, null, stage: true, repo.Directory));

        var hunk = show.GetProperty("hunks")[0];
        Assert.Equal("HEAD", hunk.GetProperty("oursLabel").GetString());
        Assert.Equal("topic", hunk.GetProperty("theirsLabel").GetString());
        Assert.Equal("main", hunk.GetProperty("ours").GetString());
        Assert.Equal("topic", hunk.GetProperty("theirs").GetString());

        var text = hunk.GetProperty("text").GetString()!;
        Assert.StartsWith("<<<<<<< HEAD", text);
        Assert.Contains("=======", text);
        Assert.Contains(">>>>>>> topic", text);
    }

    [Fact]
    public async Task Resolve_TakesTheirs_AndStagesTheFile()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo);

        var result = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "theirs", null, null, stage: true, repo.Directory));

        Assert.Equal(1, result.GetProperty("resolvedHunks").GetInt32());
        Assert.Equal(0, result.GetProperty("remainingHunks").GetInt32());
        Assert.True(result.GetProperty("staged").GetBoolean());

        Assert.Equal("line1\ntopic\nline3\n", repo.Read("a.txt").Replace("\r\n", "\n"));
        Assert.Equal("", repo.Capture("diff", "--name-only", "--diff-filter=U").Trim());

        // A staged resolution lets the merge be committed.
        ResultAssert.Succeeded(await repo.Service.CommitAsync("merge topic", stageAll: false, allowEmpty: false, repo.Directory));
    }

    [Fact]
    public async Task Resolve_OneHunkAtATime_RenumbersWhatIsLeft()
    {
        using var repo = new TestGitRepo();

        repo.Write("a.txt", "a-base\nk1\nk2\nk3\nk4\nk5\nk6\nk7\nk8\nb-base\n");
        repo.Commit("base");
        repo.Run("checkout", "-q", "-b", "topic");
        repo.Write("a.txt", "a-topic\nk1\nk2\nk3\nk4\nk5\nk6\nk7\nk8\nb-topic\n");
        repo.Commit("topic");
        repo.Run("checkout", "-q", "main");
        repo.Write("a.txt", "a-main\nk1\nk2\nk3\nk4\nk5\nk6\nk7\nk8\nb-main\n");
        repo.Commit("main");
        Assert.NotEqual(0, repo.Run("merge", "topic").exitCode);

        var first = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "list", null, "ours", null, null, stage: true, repo.Directory));
        Assert.Equal(2, first.GetProperty("files")[0].GetProperty("hunks").GetInt32());

        var one = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "ours", hunkIndex: 0, content: null, stage: false, repo.Directory));
        Assert.Equal(1, one.GetProperty("remainingHunks").GetInt32());
        Assert.False(one.GetProperty("staged").GetBoolean());

        var two = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "theirs", hunkIndex: 0, content: null, stage: true, repo.Directory));
        Assert.Equal(0, two.GetProperty("remainingHunks").GetInt32());

        Assert.Equal("a-main\nk1\nk2\nk3\nk4\nk5\nk6\nk7\nk8\nb-topic\n", repo.Read("a.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Resolve_Both_KeepsOurLinesThenTheirs()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo);

        ResultAssert.Succeeded(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "both", null, null, stage: true, repo.Directory));

        Assert.Equal("line1\nmain\ntopic\nline3\n", repo.Read("a.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Resolve_Custom_ReplacesTheWholeBlock()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo);

        ResultAssert.Succeeded(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "ours", null, content: "merged by hand", stage: true, repo.Directory));

        Assert.Equal("line1\nmerged by hand\nline3\n", repo.Read("a.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Resolve_Base_WorksOnlyWithDiff3()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo);

        var refusal = ResultAssert.Error(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "base", null, null, stage: true, repo.Directory));
        Assert.Contains("diff3", refusal);
    }

    [Fact]
    public async Task Resolve_Base_TakesTheCommonAncestorText()
    {
        using var repo = new TestGitRepo();
        MakeConflict(repo, diff3: true);

        var show = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "show", "a.txt", "ours", null, null, stage: true, repo.Directory));
        Assert.Equal("shared", show.GetProperty("hunks")[0].GetProperty("baseText").GetString());

        var result = ResultAssert.Json(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "base", null, null, stage: true, repo.Directory));
        Assert.Equal(0, result.GetProperty("remainingHunks").GetInt32());
        Assert.Equal(Base, repo.Read("a.txt").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Resolve_RefusesAFileThatHasNoMarkers()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "clean\n");
        repo.Commit("only commit");

        var error = ResultAssert.Error(await repo.Service.ConflictsAsync(
            "resolve", "a.txt", "ours", null, null, stage: true, repo.Directory));

        Assert.Contains("already resolved", error);
    }

    [Fact]
    public async Task Resolve_RefusesAPathOutsideTheRepository()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "clean\n");
        repo.Commit("only commit");

        var error = ResultAssert.Error(await repo.Service.ConflictsAsync(
            "resolve", "../../outside.txt", "ours", null, null, stage: true, repo.Directory));

        Assert.Contains("outside the repository", error);
    }
}
