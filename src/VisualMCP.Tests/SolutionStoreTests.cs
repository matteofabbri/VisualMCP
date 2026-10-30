using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Style;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The solution store: the package facts read from project.assets.json, the decompiled artifacts keyed
/// on the MVID, and the edit history with its revert. The symbol index has its own tests
/// (<see cref="SymbolIndexTests"/>): it lives in Snitch.Metacode and owns its own tables.
/// </summary>
public sealed class SolutionStoreTests
{
    // ── package facts ───────────────────────────────────────────────────────
    [Fact]
    public async Task PackageFacts_ReadsTheResolvedGraphFromTheAssetsFile()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var obj = Path.Combine(workspace.RootDirectory, "obj");
        Directory.CreateDirectory(obj);
        await File.WriteAllTextAsync(Path.Combine(obj, "project.assets.json"), """
            {
              "targets": {
                "net10.0": {
                  "Newtonsoft.Json/13.0.3": {
                    "type": "package",
                    "dependencies": { "System.Text.Json": "8.0.0" }
                  },
                  "System.Text.Json/8.0.0": { "type": "package" }
                }
              },
              "projectFileDependencyGroups": {
                "net10.0": [ "Newtonsoft.Json >= 13.0.3" ]
              }
            }
            """);

        using var store = new VisualMcpStore(workspace);
        var packages = new PackageFactsService(workspace, store);

        var refresh = ResultAssert.Json(await packages.RefreshAsync());
        Assert.Equal(2, store.PackageCount());
        Assert.Empty(refresh.GetProperty("skipped").EnumerateArray());

        var list = ResultAssert.Json(packages.List());
        var newtonsoft = list.GetProperty("packages").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetString() == "Newtonsoft.Json");
        Assert.True(newtonsoft.GetProperty("direct").GetBoolean());

        var transitive = list.GetProperty("packages").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetString() == "System.Text.Json");
        Assert.False(transitive.GetProperty("direct").GetBoolean());

        var edges = ResultAssert.Json(packages.Dependencies(packageId: "System.Text.Json"));
        var edge = edges.GetProperty("dependencies")[0];
        Assert.StartsWith("Newtonsoft.Json", edge.GetProperty("parent").GetString());
        Assert.StartsWith("System.Text.Json", edge.GetProperty("child").GetString());
    }

    [Fact]
    public async Task PackageFacts_ReportsProjectsThatHaveNeverBeenRestored()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var packages = new PackageFactsService(workspace, store);

        var refresh = ResultAssert.Json(await packages.RefreshAsync());

        Assert.Single(refresh.GetProperty("skipped").EnumerateArray());
        Assert.Contains("obj/project.assets.json", refresh.GetProperty("note").GetString());
    }

    // ── decompiled artifacts ────────────────────────────────────────────────
    [Fact]
    public void DecompiledArtifacts_AreKeyedOnTheMvidAndReused()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        // A copy: decompiling the assembly that is currently executing makes the metadata reader fight
        // the loader that has it mapped, and the host dies. The copy keeps the same module version id,
        // which is what the store keys on.
        var assembly = Path.Combine(workspace.RootDirectory, "Probe.dll");
        File.Copy(typeof(SolutionStoreTests).Assembly.Location, assembly, overwrite: true);

        var decompile = new Snitch.Metacode.Decompilation.DecompileService(store);

        var first = ResultAssert.Json(decompile.DecompileTypes(
            assembly, "SolutionStoreTests", null, 0, 1, false, null, 100_000, persist: true));
        ResultAssert.Succeeded(first);

        Assert.True(first.GetProperty("persisted").GetBoolean());
        var directory = first.GetProperty("outputDirectory").GetString()!;
        var mvid = first.GetProperty("mvid").GetString()!;
        Assert.True(Directory.Exists(directory));
        Assert.StartsWith(workspace.RootDirectory, directory);
        Assert.Equal(mvid, Assert.Single(store.Artifacts()).Mvid);

        // Same binary: the export is returned as it is, without running the decompiler again.
        var second = ResultAssert.Json(decompile.DecompileTypes(
            assembly, "SolutionStoreTests", null, 0, 1, false, null, 100_000, persist: true));

        Assert.True(second.GetProperty("reused").GetBoolean());
        Assert.Equal(directory, second.GetProperty("outputDirectory").GetString());
        Assert.Equal(mvid, second.GetProperty("mvid").GetString());
        Assert.Single(store.Artifacts());
    }

    // ── edit history and revert ─────────────────────────────────────────────
    [Fact]
    public async Task EditHistory_RevertsTheTextThatWasThereBefore()
    {
        const string source = """
            internal class C
            {
                public int F(int x) => (int)x;
            }
            """;

        using var workspace = FakeWorkspace.Create(("C.cs", source));
        using var store = new VisualMcpStore(workspace);

        var service = new SimplifyCodeService(workspace, store);
        await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0004", dryRun: false);
        Assert.Contains("=> x;", workspace.Read("C.cs"));

        var edit = Assert.Single(store.RecentEdits(10, workspace.PathOf("C.cs")));
        Assert.True(edit.Revertible);
        Assert.Equal("IDE0004", edit.Rules);

        var history = new EditHistoryService(workspace, store);
        var result = ResultAssert.Json(await history.RevertAsync(edit.Id));

        Assert.Equal(source.Replace("\r\n", "\n"), workspace.Read("C.cs").Replace("\r\n", "\n"));
        Assert.Equal(edit.Id, result.GetProperty("reverted").GetInt64());

        // The revert is itself recorded, so it can be undone in turn.
        var entries = store.RecentEdits(10, workspace.PathOf("C.cs"));
        Assert.Equal(2, entries.Count);
        Assert.Equal("revert_edit", entries[0].Tool);
    }

    [Fact]
    public async Task EditHistory_RefusesToRevertOverNewerWork()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                public int F(int x) => (int)x;
            }
            """));

        using var store = new VisualMcpStore(workspace);
        var service = new SimplifyCodeService(workspace, store);
        await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0004", dryRun: false);

        var edit = Assert.Single(store.RecentEdits(10, workspace.PathOf("C.cs")));
        await File.WriteAllTextAsync(workspace.PathOf("C.cs"), "internal class C { public int Changed => 1; }");

        var history = new EditHistoryService(workspace, store);
        var error = ResultAssert.Error(await history.RevertAsync(edit.Id));

        Assert.Contains("changed after that edit", error);
        Assert.Equal("internal class C { public int Changed => 1; }", workspace.Read("C.cs"));
    }
}
