using System.Text.Json.Nodes;
using Snitch.Languages.CSharp.Documentation;
using Snitch.Planning;
using Snitch.Storage;
using VisualMCP.Startup;
using VisualMCP.Tools.CSharp.Documentation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The docs tool is where a session learns two things that do not live in the documentation: that project
/// memory was carried over from another client's store, and what the board says to do next.
/// </summary>
public sealed class ReadProjectDocsToolTests : IDisposable
{
    private const string Project = @"D:\REPOSITORY\Demo";

    private readonly string _home;

    public ReadProjectDocsToolTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "visualmcp-docs-" + Guid.NewGuid().ToString("N"));
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
    public async Task TheFirstResponseCarriesTheMemoryReportAndLaterOnesDoNot()
    {
        var source = Path.Combine(_home, ".claude", "projects", MemoryImportOptions.ClaudeSlug(Project), "memory");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "alpha.md"), "note");
        Directory.CreateDirectory(
            Path.Combine(_home, ".zcode", "cli", "memories", "projects", "demo-0123456789abcdef", "memory"));

        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var board = new BoardService(new VisualMcpStore(workspace));
        var importer = new MemoryImporter(
            new MemoryImportOptions { HomeDirectory = _home, ProjectRoot = Project }, board);
        var docs = new FakeDocs();

        var first = Assert.IsType<JsonObject>(await ReadProjectDocsTool.ReadProjectDocs(docs, importer, board));
        var second = await ReadProjectDocsTool.ReadProjectDocs(docs, importer, board);

        Assert.NotNull(first["memoryImport"]);
        var imported = first["memoryImport"]!["imported"]!.AsArray();
        Assert.Single(imported);
        Assert.Equal("imported-project-alpha", imported[0]!.GetValue<string>());

        // The note is on the board, and the board block says so.
        var boardBlock = first["board"];
        Assert.NotNull(boardBlock);
        Assert.Equal(1, boardBlock!["memoryCount"]!.GetValue<int>());

        // The documentation itself is untouched by the merge.
        Assert.Equal(1, first["fileCount"]!.GetValue<int>());

        // The notice is delivered once; the board block still rides along because the board is not empty.
        var later = Assert.IsType<JsonObject>(second);
        Assert.Null(later["memoryImport"]);
        Assert.NotNull(later["board"]);
    }

    [Fact]
    public async Task WithoutAReportOrACardTheResponseIsTheDocumentationAsIs()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var board = new BoardService(new VisualMcpStore(workspace));
        var importer = new MemoryImporter(
            new MemoryImportOptions { HomeDirectory = _home, ProjectRoot = Project }, board);

        var result = await ReadProjectDocsTool.ReadProjectDocs(new FakeDocs(), importer, board);

        Assert.NotNull(result);
        Assert.IsNotType<JsonObject>(result);
    }

    [Fact]
    public async Task ACardOnTheBoardRidesAlongWithTheDocumentation()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var board = new BoardService(new VisualMcpStore(workspace));
        var importer = new MemoryImporter(
            new MemoryImportOptions { HomeDirectory = _home, ProjectRoot = Project }, board);
        var added = ResultAssert.Json(await board.AddAsync("Close the loop", null, "todo", "high", null, null));
        var id = added.GetProperty("id").GetInt32();

        var result = Assert.IsType<JsonObject>(
            await ReadProjectDocsTool.ReadProjectDocs(new FakeDocs(), importer, board));

        var summary = result["board"];
        Assert.NotNull(summary);
        Assert.Equal(1, summary!["openCount"]!.GetValue<int>());
        Assert.Equal(1, summary["itemCount"]!.GetValue<int>());
        Assert.Equal(id, summary["next"]!["id"]!.GetValue<int>());
        Assert.Contains("board_brief", summary["hint"]!.GetValue<string>());
    }

    private sealed class FakeDocs : IDocumentationService
    {
        public Task<object> ReadProjectDocsAsync(bool includeContent, string? nameFilter, int maxChars)
            => Task.FromResult<object>(new { fileCount = 1, files = new[] { new { path = "README.md" } } });

        public Task<object> GetXmlDocsAsync(string symbolName, string? containingType)
            => throw new NotSupportedException();

        public Task<object> FindUndocumentedAsync(string? projectName, bool includeProtected)
            => throw new NotSupportedException();
    }
}
