using System.Text.Json.Nodes;
using Snitch.Languages.CSharp.Documentation;
using VisualMCP.Startup;
using VisualMCP.Tools.CSharp.Documentation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The docs tool is where the client learns that project memory was carried over from another client's
/// store: the report rides on the first response of the session and never on a later one.
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

        var importer = new MemoryImporter(new MemoryImportOptions { HomeDirectory = _home, ProjectRoot = Project });
        var docs = new FakeDocs();

        var first = Assert.IsType<JsonObject>(await ReadProjectDocsTool.ReadProjectDocs(docs, importer));
        var second = await ReadProjectDocsTool.ReadProjectDocs(docs, importer);

        Assert.NotNull(first["memoryImport"]);
        var imported = first["memoryImport"]!["imported"]!.AsArray();
        Assert.Single(imported);
        Assert.EndsWith("alpha.md", imported[0]!.GetValue<string>());

        // The documentation itself is untouched by the merge.
        Assert.Equal(1, first["fileCount"]!.GetValue<int>());

        // The notice was delivered once: a later call is the documentation exactly as the engine returned it.
        Assert.IsNotType<JsonObject>(second);
    }

    [Fact]
    public async Task WithoutAReportTheResponseIsTheDocumentationAsIs()
    {
        var importer = new MemoryImporter(new MemoryImportOptions { HomeDirectory = _home, ProjectRoot = Project });

        var result = await ReadProjectDocsTool.ReadProjectDocs(new FakeDocs(), importer);

        Assert.NotNull(result);
        Assert.IsNotType<JsonObject>(result);
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
