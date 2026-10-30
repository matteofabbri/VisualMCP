using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>The on-disk context store: a SQLite file under &lt;solution&gt;/.visualMCP.</summary>
public sealed class StoreTests
{
    [Fact]
    public void SetAndGetRoundTrip()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        store.Set("inspection:probe", "{\"value\":1}", "stamp-1");

        var entry = store.Get("inspection:probe");
        Assert.NotNull(entry);
        Assert.Equal("{\"value\":1}", entry!.Value);
        Assert.Equal("stamp-1", entry.Stamp);

        Assert.NotNull(store.DatabasePath);
        Assert.EndsWith(".visualMCP/visualmcp.db", store.DatabasePath!.Replace('\\', '/'));
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public void OverwritingKeepsOneRowAndTheNewStamp()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        store.Set("key", "first", "stamp-1");
        store.Set("key", "second", "stamp-2");

        var entry = store.Get("key");
        Assert.Equal("second", entry!.Value);
        Assert.Equal("stamp-2", entry.Stamp);
    }

    [Fact]
    public void RemoveDeletesTheFact()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        store.Set("key", "value");
        Assert.True(store.Remove("key"));
        Assert.Null(store.Get("key"));
        Assert.False(store.Remove("key"));
    }

    [Fact]
    public void DataSurvivesAFreshStoreInstance()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));

        using (var first = new VisualMcpStore(workspace))
        {
            first.Set("key", "value", "stamp");
        }

        using var second = new VisualMcpStore(workspace);
        var entry = second.Get("key");
        Assert.NotNull(entry);
        Assert.Equal("value", entry!.Value);
        Assert.Equal("stamp", entry.Stamp);
    }

    [Fact]
    public void MissingKeyIsNull()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        Assert.Null(store.Get("never-written"));
    }
}
