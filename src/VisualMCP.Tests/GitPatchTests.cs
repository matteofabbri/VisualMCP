using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Patches as named shelves: create from a diff, find and inspect it later, apply (checking first),
/// reverse it, delete it.
/// </summary>
public sealed class GitPatchTests
{
    [Fact]
    public async Task Patch_RoundTripsAChangeThroughDisk()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "v1\n");
        repo.Commit("base");
        repo.Write("a.txt", "v2\n");

        var created = ResultAssert.Json(await repo.Service.PatchAsync(
            "create", "wip.patch", null, null, reverse: false, check: false, confirm: false, directory: null, repo.Directory));
        Assert.Equal("wip.patch", created.GetProperty("name").GetString());
        Assert.Contains("a.txt", created.GetProperty("files").EnumerateArray().Select(f => f.GetString()));

        var list = ResultAssert.Json(await repo.Service.PatchAsync(
            "list", null, null, null, false, false, false, null, repo.Directory));
        Assert.Equal(1, list.GetProperty("count").GetInt32());
        Assert.Equal("wip.patch", list.GetProperty("patches")[0].GetProperty("name").GetString());

        var show = ResultAssert.Json(await repo.Service.PatchAsync(
            "show", "wip.patch", null, null, false, false, false, null, repo.Directory));
        Assert.Contains("diff --git", show.GetProperty("text").GetString());

        // Throw the working-tree change away, then bring it back from the shelf.
        Assert.Equal(0, repo.Run("checkout", "--", "a.txt").exitCode);
        Assert.Equal("v1", repo.Read("a.txt").Trim());

        var check = ResultAssert.Json(await repo.Service.PatchAsync(
            "apply", "wip.patch", null, null, false, check: true, false, null, repo.Directory));
        Assert.False(check.GetProperty("applied").GetBoolean());
        Assert.Equal("v1", repo.Read("a.txt").Trim());

        var apply = ResultAssert.Json(await repo.Service.PatchAsync(
            "apply", "wip.patch", null, null, false, false, false, null, repo.Directory));
        Assert.True(apply.GetProperty("applied").GetBoolean());
        Assert.Equal("v2", repo.Read("a.txt").Trim());

        var reverse = ResultAssert.Json(await repo.Service.PatchAsync(
            "apply", "wip.patch", null, null, reverse: true, false, false, null, repo.Directory));
        Assert.True(reverse.GetProperty("reversed").GetBoolean());
        Assert.Equal("v1", repo.Read("a.txt").Trim());
    }

    [Fact]
    public async Task Patch_DeleteNeedsConfirmation()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "v1\n");
        repo.Commit("base");
        repo.Write("a.txt", "v2\n");

        ResultAssert.Succeeded(await repo.Service.PatchAsync(
            "create", "wip.patch", null, null, false, false, false, null, repo.Directory));

        var refusal = ResultAssert.Error(await repo.Service.PatchAsync(
            "delete", "wip.patch", null, null, false, false, confirm: false, null, repo.Directory));
        Assert.Contains("confirm=true", refusal);

        ResultAssert.Succeeded(await repo.Service.PatchAsync(
            "delete", "wip.patch", null, null, false, false, confirm: true, null, repo.Directory));

        var list = ResultAssert.Json(await repo.Service.PatchAsync(
            "list", null, null, null, false, false, false, null, repo.Directory));
        Assert.Equal(0, list.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Patch_CreateFromStagedChanges()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "v1\n");
        repo.Commit("base");
        repo.Write("a.txt", "v2\n");
        Assert.Equal(0, repo.Run("add", "a.txt").exitCode);

        var created = ResultAssert.Json(await repo.Service.PatchAsync(
            "create", "staged.patch", "staged", null, false, false, false, null, repo.Directory));
        Assert.Equal("staged", created.GetProperty("source").GetString());

        var show = ResultAssert.Json(await repo.Service.PatchAsync(
            "show", "staged.patch", null, null, false, false, false, null, repo.Directory));
        Assert.Contains("+v2", show.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Patch_EmptyDiff_IsRefused()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "v1\n");
        repo.Commit("base");

        var error = ResultAssert.Error(await repo.Service.PatchAsync(
            "create", null, null, null, false, false, false, null, repo.Directory));

        Assert.Contains("no diff", error);
    }

    [Fact]
    public async Task Patch_NameCannotEscapeThePatchFolder()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "v1\n");
        repo.Commit("base");
        repo.Write("a.txt", "v2\n");

        var error = ResultAssert.Error(await repo.Service.PatchAsync(
            "create", "../../evil.patch", null, null, false, false, false, null, repo.Directory));

        Assert.Contains("not a path", error);
    }

    [Fact]
    public async Task Patch_ShowOfAMissingFile_IsRefused()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.txt", "v1\n");
        repo.Commit("base");

        var error = ResultAssert.Error(await repo.Service.PatchAsync(
            "show", "nope.patch", null, null, false, false, false, null, repo.Directory));

        Assert.Contains("not found", error);
    }
}
