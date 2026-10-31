using System.Text.Json;
using Snitch.Planning;
using Snitch.Storage;
using VisualMCP.Startup;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Importing project memory onto the board: the notes that used to live as files in a client's store become
/// <c>board_memory</c> rows (topic from the frontmatter, body from the note), a second pass imports nothing,
/// and the files themselves are left where they are — the client owns them.
/// </summary>
public sealed class MemoryImportTests : IDisposable
{
    private const string Project = @"D:\REPOSITORY\Demo";

    private readonly string _home;
    private readonly FakeWorkspace _workspace;
    private readonly BoardService _board;

    public MemoryImportTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "visualmcp-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        _board = new BoardService(new VisualMcpStore(_workspace));
    }

    /// <summary>The temporary trees are the test's own artifacts; a failed delete must not fail the test.</summary>
    public void Dispose()
    {
        _workspace.Dispose();
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task ImportsTheNotesOntoTheBoardAndLeavesTheFiles()
    {
        var store = ClaudeStore();
        Write(Path.Combine(store, "alpha.md"),
            "---\nname: alpha\ntype: project\ndescription: how the thing works\n---\nThe body of alpha.");
        Write(Path.Combine(store, "MEMORY.md"), "- [Alpha](alpha.md) — a\n");

        var report = await Importer().RunAsync();

        Assert.True(report.HasSources);
        Assert.Equal(["imported-project-alpha"], report.Imported);
        Assert.True(report.ImportedAnything);

        var note = await Recall("imported-project-alpha");
        Assert.Equal("fact", note.GetProperty("kind").GetString());
        Assert.Equal("The body of alpha.", note.GetProperty("body").GetString());

        // The file is the client's: imported, not moved; the index is not a note.
        Assert.True(File.Exists(Path.Combine(store, "alpha.md")));
        Assert.Equal(["imported-project-alpha"], await Topics());
    }

    [Fact]
    public async Task ImportsFromBothStoresAndASecondPassImportsNothing()
    {
        Write(Path.Combine(ClaudeStore(), "alpha.md"), "---\nname: alpha\n---\nbody");
        Write(Path.Combine(ClientStore("demo-0123456789abcdef"), "beta.md"), "---\nname: beta\n---\nother");

        var first = await Importer().RunAsync();
        var second = await Importer().RunAsync();

        Assert.Equal(new[] { "imported-project-alpha", "imported-project-beta" }, first.Imported);
        Assert.Empty(second.Imported);
        Assert.Equal(2, second.AlreadyPresent.Count);
    }

    [Fact]
    public async Task TheMemoryTypesMapOntoTheBoardKinds()
    {
        var store = ClaudeStore();
        Write(Path.Combine(store, "pref.md"), "---\nname: style\ntype: user\n---\nPrefer Italian.");
        Write(Path.Combine(store, "rule.md"), "---\nname: never-lock\ntype: feedback\n---\nNo locks.");

        await Importer().RunAsync();

        Assert.Equal("preference", (await Recall("imported-user-style")).GetProperty("kind").GetString());
        Assert.Equal("constraint", (await Recall("imported-feedback-never-lock")).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ANestedMetadataTypeIsReadToo()
    {
        // This client writes `type` under `metadata:`; the older Claude notes put it at the top level.
        Write(Path.Combine(ClaudeStore(), "rule.md"),
            "---\nname: no-locks\ndescription: x\nmetadata:\n  node_type: memory\n  type: feedback\n---\nNo locks.");

        await Importer().RunAsync();

        Assert.Equal("constraint", (await Recall("imported-feedback-no-locks")).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task WithoutNotesNothingHappens()
    {
        var report = await Importer().RunAsync();

        Assert.False(report.HasSources);
        Assert.Empty(report.Imported);
        Assert.False(report.ImportedAnything);
    }

    [Fact]
    public async Task OversizedNotesAreReportedNotImported()
    {
        Write(Path.Combine(ClaudeStore(), "big.md"), new string('x', 200));

        var report = await Importer(maxFileBytes: 16).RunAsync();

        Assert.Single(report.Oversized);
        Assert.Empty(report.Imported);
    }

    [Fact]
    public async Task ThePassCanBeSwitchedOff()
    {
        Write(Path.Combine(ClaudeStore(), "alpha.md"), "note");

        var report = await Importer(enabled: false).RunAsync();

        Assert.False(report.Enabled);
        Assert.Empty(report.Sources);
        Assert.Empty(report.Imported);
    }

    [Fact]
    public async Task TheNoticeIsDeliveredOnceAndOnlyWhenSomethingMoved()
    {
        Write(Path.Combine(ClaudeStore(), "alpha.md"), "note");
        var importer = Importer();

        Assert.NotNull(await importer.TakeNoticeAsync());
        Assert.Null(await importer.TakeNoticeAsync());
    }

    [Fact]
    public async Task ThereIsNoNoticeWhenNothingWasImported()
    {
        Assert.Null(await Importer().TakeNoticeAsync());
    }

    [Fact]
    public void ClaudeSlugMatchesTheClientsDirectoryNaming()
    {
        Assert.Equal("D--REPOSITORY-VisualMCP", MemoryImportOptions.ClaudeSlug(@"D:\REPOSITORY\VisualMCP"));
        Assert.Equal("D--REPOSITORY-VisualMCP", MemoryImportOptions.ClaudeSlug(@"D:\REPOSITORY\VisualMCP\"));
    }

    private MemoryImporter Importer(int maxFileBytes = 512 * 1024, bool enabled = true)
        => new(new MemoryImportOptions
        {
            HomeDirectory = _home,
            ProjectRoot = Project,
            MaxFileBytes = maxFileBytes,
            Enabled = enabled,
        }, _board);

    private async Task<JsonElement> Recall(string topic)
    {
        var json = ResultAssert.Json(await _board.RecallAsync(topic, null, null, null, false, 10));
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        return json.GetProperty("items")[0];
    }

    private async Task<string[]> Topics()
    {
        var json = ResultAssert.Json(await _board.RecallAsync(null, null, null, null, false, 50));
        return json.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("topic").GetString()!)
            .ToArray();
    }

    private string ClaudeStore()
    {
        var directory = Path.Combine(
            _home, ".claude", "projects", MemoryImportOptions.ClaudeSlug(Project), "memory");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string ClientStore(string name)
    {
        var directory = Path.Combine(_home, ".zcode", "cli", "memories", "projects", name, "memory");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Write(string path, string content) => File.WriteAllText(path, content);
}
