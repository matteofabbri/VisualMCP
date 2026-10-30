using Snitch.Search;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The full-text index: what a search answers (file:line, the line, the files that match most), how it
/// stays current between builds, and that a decompiled artifact is searchable as soon as it is written.
/// </summary>
public sealed class TextIndexTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(params (string Name, string Source)[] files)
        {
            Workspace = FakeWorkspace.Create(files);
            Store = new VisualMcpStore(Workspace);
            Service = new FullTextIndexService(Workspace, Store);
        }

        public FakeWorkspace Workspace { get; }

        public VisualMcpStore Store { get; }

        public FullTextIndexService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            Store.Dispose();
            Workspace.Dispose();
        }
    }

    [Fact]
    public async Task Search_AnswersWithTheFileAndTheLine()
    {
        using var harness = new Harness(
            ("Counter.cs", """
                internal class Counter
                {
                    public void Add(int delta)
                    {
                        Console.WriteLine(delta);
                    }
                }
                """),
            ("Other.cs", """
                internal class Other
                {
                    public int Value => 42;
                }
                """));

        var build = ResultAssert.Json(await harness.Service.BuildAsync());
        // Two sources plus the Probe.csproj FakeWorkspace writes next to them, which is indexed as source.
        Assert.Equal(3, build.GetProperty("indexed").GetInt32());

        var found = ResultAssert.Json(await harness.Service.SearchAsync("Console.WriteLine"));
        var hit = Assert.Single(found.GetProperty("hits").EnumerateArray());

        Assert.Equal(harness.Workspace.PathOf("Counter.cs"), hit.GetProperty("file").GetString());
        Assert.Equal(5, hit.GetProperty("line").GetInt32());
        Assert.Contains("Console.WriteLine(delta)", hit.GetProperty("text").GetString());
        Assert.Equal(harness.Workspace.PathOf("Counter.cs"),
            found.GetProperty("files")[0].GetProperty("file").GetString());
    }

    [Fact]
    public async Task A_word_inside_an_identifier_is_found()
    {
        using var harness = new Harness(("Composition.cs", """
            internal sealed class Composition
            {
                public void Wire(IServiceCollection services) => services.AddSingleton<IFullTextIndexService, FullTextIndexService>();
            }
            """));

        await harness.Service.BuildAsync();

        // Both query words sit inside camel-cased identifiers: FTS5 alone would see them as one token.
        var found = ResultAssert.Json(await harness.Service.SearchAsync("full text index"));
        var hit = Assert.Single(found.GetProperty("hits").EnumerateArray());
        Assert.Equal(3, hit.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task A_second_build_reindexes_only_what_changed_and_a_deleted_file_stops_answering()
    {
        using var harness = new Harness(("A.cs", "internal class A { }"), ("B.cs", "internal class B { }"));
        await harness.Service.BuildAsync();

        var unchanged = ResultAssert.Json(await harness.Service.BuildAsync());
        Assert.Equal(0, unchanged.GetProperty("indexed").GetInt32());
        // A.cs, B.cs and the Probe.csproj the workspace writes.
        Assert.Equal(3, unchanged.GetProperty("unchanged").GetInt32());

        File.WriteAllText(harness.Workspace.PathOf("B.cs"), "internal class B { int Changed => 1; }");
        var edited = ResultAssert.Json(await harness.Service.BuildAsync());
        Assert.Equal(1, edited.GetProperty("indexed").GetInt32());
        // A.cs and the Probe.csproj; only B.cs changed.
        Assert.Equal(2, edited.GetProperty("unchanged").GetInt32());

        var found = ResultAssert.Json(await harness.Service.SearchAsync("changed"));
        Assert.Single(found.GetProperty("hits").EnumerateArray());

        File.Delete(harness.Workspace.PathOf("B.cs"));
        var pruned = ResultAssert.Json(await harness.Service.BuildAsync());
        Assert.Equal(1, pruned.GetProperty("removed").GetInt32());

        var gone = ResultAssert.Json(await harness.Service.SearchAsync("changed"));
        Assert.Empty(gone.GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public async Task A_written_decompiled_file_is_searchable_without_a_build()
    {
        using var harness = new Harness(("A.cs", "internal class A { }"));
        await harness.Service.BuildAsync();

        var file = harness.Workspace.PathOf("Decompiled.cs");
        const string source = "internal sealed class Decompiled { public int Answer() => 42; }";
        File.WriteAllText(file, source);
        harness.Service.IndexText(file, "decompiled", source);

        var found = ResultAssert.Json(await harness.Service.SearchAsync("answer"));
        var hit = Assert.Single(found.GetProperty("hits").EnumerateArray());
        Assert.Equal(file, hit.GetProperty("file").GetString());

        var files = ResultAssert.Json(harness.Service.Documents("decompiled", 10));
        Assert.Equal(file, Assert.Single(files.GetProperty("documents").EnumerateArray()).GetProperty("file").GetString());
    }

    [Fact]
    public async Task Context_and_exclusions_are_applied()
    {
        using var harness = new Harness(("C.cs", """
            internal class C
            {
                // first
                public int Alpha() => 1;
                public int Beta() => 2;
            }
            """));

        await harness.Service.BuildAsync();

        var withContext = ResultAssert.Json(await harness.Service.SearchAsync("Alpha", contextLines: 1));
        var hit = Assert.Single(withContext.GetProperty("hits").EnumerateArray());
        var context = hit.GetProperty("context").EnumerateArray().ToList();
        Assert.Equal(3, context.Count);
        Assert.Contains("first", context[0].GetProperty("text").GetString());

        var excluded = ResultAssert.Json(await harness.Service.SearchAsync("public -Beta"));
        Assert.All(excluded.GetProperty("hits").EnumerateArray(),
            match => Assert.DoesNotContain("Beta", match.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task An_empty_index_is_built_by_the_first_search()
    {
        using var harness = new Harness(("C.cs", "internal class C { int Alpha => 1; }"));

        var found = ResultAssert.Json(await harness.Service.SearchAsync("alpha"));

        Assert.NotNull(found.GetProperty("built"));
        // C.cs and the Probe.csproj the workspace writes.
        Assert.Equal(2, found.GetProperty("index").GetProperty("documents").GetInt32());
        Assert.Single(found.GetProperty("hits").EnumerateArray());
    }

    [Fact]
    public void Status_reportsWhatIsStored()
    {
        using var harness = new Harness(("C.cs", "internal class C { }"), ("README.md", "# Title"));
        harness.Service.BuildAsync().GetAwaiter().GetResult();

        var status = ResultAssert.Json(harness.Service.Status());

        Assert.True(status.GetProperty("available").GetBoolean());
        // C.cs, README.md and the Probe.csproj the workspace writes (counted as source).
        Assert.Equal(3, status.GetProperty("documents").GetInt32());
        Assert.Equal(2, status.GetProperty("source").GetInt32());
        Assert.Equal(1, status.GetProperty("documentation").GetInt32());
        Assert.EndsWith("visualmcp.db", status.GetProperty("database").GetString());
    }

    [Fact]
    public void The_query_builder_ands_prefixes_and_honours_exclusions()
    {
        Assert.Equal("\"Get Required Service\"*", FullTextIndexService.LiteralExpression("GetRequiredService"));
        Assert.Equal("\"alpha\"* AND \"beta\"*", FullTextIndexService.LiteralExpression("alpha beta"));
        Assert.Equal("\"alpha\"* NOT (\"beta\"*)", FullTextIndexService.LiteralExpression("alpha -beta"));
        Assert.Equal("\"exact phrase\"", FullTextIndexService.LiteralExpression("\"exact phrase\""));
    }
}
