using Snitch.Languages.CSharp.Refactoring;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Local history over the solution store: the same file is captured once, again only after it changes,
/// and a snapshot can be diffed and restored — with the replaced text captured first.
/// </summary>
public sealed class LocalHistoryTests
{
    [Fact]
    public async Task A_snapshot_captures_a_file_once_and_again_only_after_it_changes()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { public int Value = 1; }"));
        using var store = new VisualMcpStore(workspace);
        using var history = new LocalHistoryService(workspace, store);
        var path = workspace.PathOf("C.cs");

        var first = ResultAssert.Json(await history.SnapshotAsync());
        Assert.True(first.GetProperty("captured").GetInt32() >= 1);

        var before = ResultAssert.Json(await history.HistoryAsync(path));
        var snapshot = Assert.Single(before.GetProperty("snapshots").EnumerateArray());
        var id = snapshot.GetProperty("id").GetInt64();
        Assert.Equal(
            before.GetProperty("current").GetProperty("hash").GetString(),
            snapshot.GetProperty("hash").GetString());

        // The change goes through the same writer the tools use, which keeps the modelled text in step.
        await RefactoringFileWriter.WriteAndUpdateAsync(workspace, path, "internal class C { public int Value = 2; }");

        var second = ResultAssert.Json(await history.SnapshotAsync());
        Assert.Equal(1, second.GetProperty("captured").GetInt32());
        Assert.Contains(second.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("file").GetString() == path);

        var after = ResultAssert.Json(await history.HistoryAsync(path));
        Assert.Equal(2, after.GetProperty("snapshots").GetArrayLength());

        var diff = ResultAssert.Json(await history.DiffAsync(path, id));
        Assert.Contains("Value = 1", diff.GetProperty("diff").GetString());
        Assert.Contains("Value = 2", diff.GetProperty("diff").GetString());
    }

    [Fact]
    public async Task A_restore_writes_the_snapshot_back_and_is_recorded()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { public int Value = 1; }"));
        using var store = new VisualMcpStore(workspace);
        using var history = new LocalHistoryService(workspace, store);
        var path = workspace.PathOf("C.cs");

        await history.SnapshotAsync();
        var before = ResultAssert.Json(await history.HistoryAsync(path));
        var id = before.GetProperty("snapshots")[0].GetProperty("id").GetInt64();

        await RefactoringFileWriter.WriteAndUpdateAsync(workspace, path, "internal class C { public int Value = 2; }");
        await history.SnapshotAsync();

        var restored = ResultAssert.Json(await history.RestoreAsync(path, id, dryRun: false));
        Assert.True(restored.GetProperty("applied").GetBoolean());
        Assert.Equal("internal class C { public int Value = 1; }", workspace.Read("C.cs"));

        // The restore is an edit like any other, so it shows up in the edit history and can be undone.
        Assert.Contains(store.RecentEdits(10, path), edit => edit.Tool == "local_history_restore");
    }

    [Fact]
    public async Task A_restore_dry_run_reports_the_diff_and_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { public int Value = 1; }"));
        using var store = new VisualMcpStore(workspace);
        using var history = new LocalHistoryService(workspace, store);
        var path = workspace.PathOf("C.cs");

        await history.SnapshotAsync();
        var before = ResultAssert.Json(await history.HistoryAsync(path));
        var id = before.GetProperty("snapshots")[0].GetProperty("id").GetInt64();

        await RefactoringFileWriter.WriteAndUpdateAsync(workspace, path, "internal class C { public int Value = 2; }");

        var preview = ResultAssert.Json(await history.RestoreAsync(path, id));
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Contains("Value = 1", preview.GetProperty("diff").GetString());
        Assert.Equal("internal class C { public int Value = 2; }", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task History_of_an_unknown_file_says_so_rather_than_failing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        using var history = new LocalHistoryService(workspace, store);

        var result = ResultAssert.Json(await history.HistoryAsync(Path.Combine(workspace.RootDirectory, "missing.cs")));

        Assert.False(result.GetProperty("exists").GetBoolean());
        Assert.Equal(0, result.GetProperty("returned").GetInt32());
    }
}
