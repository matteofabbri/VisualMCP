using VisualMCP.Startup;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Carrying project memory across clients: notes that sit in another client's store
/// (<c>~/.claude/projects/&lt;slug&gt;/memory</c>) reach the one this client reads, together with the index
/// lines it is missing — and nothing else is touched.
/// </summary>
public sealed class MemoryImportTests : IDisposable
{
    private const string Project = @"D:\REPOSITORY\Demo";

    private readonly string _home;

    public MemoryImportTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "visualmcp-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    /// <summary>The temporary tree is the test's own artifact; a failed delete must not fail the test.</summary>
    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public async Task CarriesMissingNotesAndTheirIndexLines()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "MEMORY.md"), "- [Alpha](alpha.md) — a\n- [Beta](beta.md) — b\n");
        Write(Path.Combine(source, "alpha.md"), "---\nname: alpha\n---\nbody of alpha");
        Write(Path.Combine(source, "beta.md"), "the client already knows this");

        var store = ClientStore("demo-0123456789abcdef");
        Write(Path.Combine(store, "MEMORY.md"), "# Memory Index\n\n- [Beta](beta.md) — b\n");
        Write(Path.Combine(store, "beta.md"), "the client already knows this");

        var report = await Importer().RunAsync();

        Assert.True(report.HasSources);
        Assert.Single(report.Sources);
        Assert.Equal(store, report.Target);
        Assert.Equal(new[] { Path.Combine(store, "alpha.md") }, report.Imported);
        Assert.Equal(new[] { Path.Combine(store, "beta.md") }, report.AlreadyPresent);
        Assert.Equal(1, report.IndexLinesAdded);
        Assert.True(report.ImportedAnything);

        Assert.Equal("---\nname: alpha\n---\nbody of alpha", File.ReadAllText(Path.Combine(store, "alpha.md")));
        Assert.Equal("the client already knows this", File.ReadAllText(Path.Combine(store, "beta.md")));

        var index = File.ReadAllText(Path.Combine(store, "MEMORY.md"));
        Assert.Contains("(alpha.md)", index);
        Assert.Equal(1, CountOf(index, "(beta.md)"));
    }

    [Fact]
    public async Task SecondRunImportsNothing()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "MEMORY.md"), "- [Alpha](alpha.md) — a\n");
        Write(Path.Combine(source, "alpha.md"), "note");
        ClientStore("demo-0123456789abcdef");

        var first = await Importer().RunAsync();
        var second = await Importer().RunAsync();

        Assert.Single(first.Imported);
        Assert.Empty(second.Imported);
        Assert.Equal(0, second.IndexLinesAdded);
        Assert.Single(second.AlreadyPresent);
    }

    [Fact]
    public async Task WithoutNotesInAnotherStoreNothingHappens()
    {
        ClientStore("demo-0123456789abcdef");

        var report = await Importer().RunAsync();

        Assert.False(report.HasSources);
        Assert.Null(report.Target);
        Assert.Empty(report.Imported);
        Assert.False(report.ImportedAnything);
    }

    [Fact]
    public async Task NotesWithoutAClientStoreAreReportedAndLeftAlone()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "alpha.md"), "note");

        var report = await Importer().RunAsync();

        Assert.True(report.HasSources);
        Assert.Null(report.Target);
        Assert.Empty(report.Imported);
        Assert.NotEmpty(report.Notes);
        Assert.True(File.Exists(Path.Combine(source, "alpha.md")));
    }

    [Fact]
    public async Task TwoMatchingClientStoresAreRefusedRatherThanGuessed()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "alpha.md"), "note");
        ClientStore("demo-aaa");
        ClientStore("demo-bbb");

        var report = await Importer().RunAsync();

        Assert.Null(report.Target);
        Assert.Empty(report.Imported);
        Assert.Contains(report.Notes, note => note.Contains("2 memory stores", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedNotesAreReportedNotCopied()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "alpha.md"), new string('x', 200));
        var store = ClientStore("demo-0123456789abcdef");

        var report = await Importer(maxFileBytes: 16).RunAsync();

        Assert.Single(report.Oversized);
        Assert.Empty(report.Imported);
        Assert.False(File.Exists(Path.Combine(store, "alpha.md")));
    }

    [Fact]
    public async Task ThePassCanBeSwitchedOff()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "alpha.md"), "note");
        var store = ClientStore("demo-0123456789abcdef");

        var report = await Importer(enabled: false).RunAsync();

        Assert.False(report.Enabled);
        Assert.Empty(report.Sources);
        Assert.False(File.Exists(Path.Combine(store, "alpha.md")));
    }

    [Fact]
    public async Task TheNoticeIsDeliveredOnceAndOnlyWhenSomethingMoved()
    {
        var source = SourceDirectory();
        Write(Path.Combine(source, "alpha.md"), "note");
        ClientStore("demo-0123456789abcdef");

        var importer = Importer();

        Assert.NotNull(await importer.TakeNoticeAsync());
        Assert.Null(await importer.TakeNoticeAsync());
    }

    [Fact]
    public async Task ThereIsNoNoticeWhenNothingWasImported()
    {
        ClientStore("demo-0123456789abcdef");

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
        });

    private string SourceDirectory()
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

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }
}
