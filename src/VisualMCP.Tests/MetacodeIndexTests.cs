using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Snitch.Metacode.Indexing;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The solution's metacode as a persisted artifact: built from the files, skipped when nothing changed,
/// replaced when a file does, and read back as complexity and as generated C#.
/// </summary>
public sealed class MetacodeIndexTests
{
    private const string Counter = """
        namespace Demo;

        public static class Counter
        {
            public static int Sum(int n)
            {
                var total = 0;
                for (var i = 0; i < n; i++)
                {
                    if (i % 2 == 0)
                    {
                        total += i;
                    }
                }

                return total;
            }
        }
        """;

    private const string Shapes = """
        namespace Demo;

        public sealed class Shape
        {
            public string Name { get; set; } = "shape";

            public double Area(double size) => size * size;
        }
        """;

    private static (FakeWorkspace Workspace, VisualMcpStore Store, MetacodeIndexService Service) Create()
    {
        var workspace = FakeWorkspace.Create(("Counter.cs", Counter), ("Shapes.cs", Shapes));
        var store = new VisualMcpStore(workspace);
        return (workspace, store, new MetacodeIndexService(workspace, store));
    }

    [Fact]
    public async Task A_build_translates_every_document_and_a_second_build_skips_them()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            var first = await service.BuildAsync();
            Assert.Null(first.Error);
            Assert.Equal(2, first.Scanned);
            Assert.Equal(2, first.Indexed);
            Assert.Equal(0, first.Skipped);
            Assert.True(first.Declarations >= 5, $"expected the declarations of both files, got {first.Declarations}");
            Assert.Equal(2, first.Types);
            Assert.NotNull(first.Database);

            var second = await service.BuildAsync();
            Assert.Equal(0, second.Indexed);
            Assert.Equal(2, second.Skipped);
        }
    }

    [Fact]
    public async Task Saving_a_file_replaces_only_its_rows()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            await service.BuildAsync();

            var path = workspace.PathOf("Counter.cs");
            var before = service.Trees(path: path);
            Assert.NotEmpty(before);
            Assert.Contains(before, row => row.Text.Contains("RETURN total", StringComparison.Ordinal));

            File.WriteAllText(path, Counter.Replace("return total;", "return total + 1;"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            var refresh = await service.BuildAsync();
            Assert.Equal(1, refresh.Indexed);
            Assert.Equal(1, refresh.Skipped);

            var after = service.Trees(path: path);
            Assert.NotEmpty(after);
            Assert.Contains(after, row => row.Text.Contains("RETURN total + 1", StringComparison.Ordinal));

            // Both halves of the record moved: the metacode, and the native text the store kept.
            Assert.Contains("return total + 1", service.Source(path)!.Value.Text);

            // The other file kept its rows: an edit is per document, not a rebuild.
            Assert.NotEmpty(service.Trees(path: workspace.PathOf("Shapes.cs")));
        }
    }

    [Fact]
    public async Task The_stored_metacode_is_the_source_of_the_complexity_report()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            await service.BuildAsync();

            var report = service.Complexity(minCyclomatic: 1);
            var sum = report.Single(row => row.Name == "Sum");

            // Baseline 1 + the loop + the if inside it: the path count the model sees is three.
            Assert.Equal(3, sum.Cyclomatic);
            Assert.Equal("Low", sum.Risk);
            Assert.Contains(report, row => row.Name == "Area" && row.Cyclomatic == 1);

            var status = service.Status();
            Assert.Equal(2, status.Documents);
            Assert.True(status.Functions >= 2);
            Assert.Equal(2, status.Types);
            Assert.Equal(3, status.MaxCyclomatic);
        }
    }

    [Fact]
    public async Task A_stored_symbol_can_be_generated_back_to_CSharp()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            await service.BuildAsync();

            var result = service.Generate(symbol: "Sum");

            Assert.Null(result.Error);
            Assert.Contains("int Sum(int n)", result.Code, StringComparison.Ordinal);
            Assert.Contains("return total;", result.Code, StringComparison.Ordinal);
            Assert.Empty(SyntaxErrors("public static class Host\n{\n" + result.Code + "\n}\n"));
        }
    }

    [Fact]
    public async Task Pseudocode_can_be_generated_into_a_CSharp_file()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            var result = service.Generate(text: """
                MODULE Demo
                CLASS Calculator
                PUBLIC METHOD Add(a : int, b : int) : int
                RETURN a + b
                END METHOD
                END CLASS
                END MODULE
                """);

            Assert.Null(result.Error);
            Assert.Contains("class Calculator", result.Code, StringComparison.Ordinal);
            Assert.Contains("int Add(int a, int b)", result.Code, StringComparison.Ordinal);
            Assert.Contains("return a + b;", result.Code, StringComparison.Ordinal);
            Assert.Empty(SyntaxErrors(result.Code));
        }
    }

    [Fact]
    public async Task An_unknown_target_is_refused_by_the_service()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            var result = service.Generate(text: "MODULE m\nFUNCTION f()\nRETURN 1\nEND FUNCTION\nEND MODULE", language: "python");

            Assert.NotNull(result.Error);
            Assert.Empty(result.Code);
        }
    }

    [Fact]
    public async Task Every_source_is_stored_in_its_native_language_next_to_its_metacode()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            var report = await service.BuildAsync();

            Assert.Null(report.Error);
            Assert.Equal(2, report.Sources);
            Assert.Equal(0, report.Unsupported);

            // The native half: the file exactly as it is on disk, tagged with the language it is written in.
            Assert.Equal(("cs", Counter), service.Source(workspace.PathOf("Counter.cs"))!.Value);
            Assert.Equal(("cs", Shapes), service.Source(workspace.PathOf("Shapes.cs"))!.Value);

            // The metacode half of the same file: the trees the model produced from it.
            Assert.NotEmpty(service.Trees(path: workspace.PathOf("Counter.cs")));

            var status = service.Status();
            Assert.Equal(2, status.Sources);
            Assert.Equal(2, status.Documents);
        }
    }

    [Fact]
    public async Task A_Visual_Basic_file_is_read_like_any_other_source()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Module1.vb", """
            Public Module Module1
                Public Function Sum(n As Integer) As Integer
                    Return n + 1
                End Function
            End Module
            """);
        using var store = new VisualMcpStore(workspace);
        using var service = new MetacodeIndexService(workspace, store);

        var report = await service.BuildAsync();

        Assert.Null(report.Error);
        Assert.Equal(1, report.Scanned);
        Assert.Equal(1, report.Indexed);
        Assert.Equal(0, report.Unsupported);

        // The native half: the file in its own language.
        var source = service.Source(workspace.PathOf("Module1.vb"));
        Assert.NotNull(source);
        Assert.Equal("vb", source.Value.Language);
        Assert.Contains("Public Module Module1", source.Value.Text);

        // The metacode half: a Visual Basic frontend reads it, so the trees are there.
        Assert.NotEmpty(service.Trees(path: workspace.PathOf("Module1.vb")));
        Assert.Equal(1, service.Status().Documents);

        // A second pass skips it on the timestamp, like any other unchanged file.
        var again = await service.BuildAsync();
        Assert.Equal(1, again.Skipped);
    }

    [Fact]
    public async Task A_Razor_page_is_found_on_disk_and_stored_like_any_other_source()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            // Not a Roslyn document — a content item — so the pass has to see it without the workspace.
            var page = workspace.PathOf("Index.cshtml");
            File.WriteAllText(page, "@page\n@model Demo.Model\n<h1>@Model.Title</h1>\n");

            var report = await service.BuildAsync();

            Assert.Null(report.Error);
            Assert.Equal(3, report.Scanned);
            Assert.Equal(3, report.Sources);
            Assert.Equal(0, report.Unsupported);

            var source = service.Source(page);
            Assert.NotNull(source);
            Assert.Equal("razor", source.Value.Language);
            Assert.NotEmpty(service.Trees(path: page));
        }
    }

    [Fact]
    public async Task A_store_written_before_the_native_half_existed_is_filled_in_on_the_next_pass()
    {
        var (workspace, store, service) = Create();
        using (workspace)
        using (store)
        using (service)
        {
            await service.BuildAsync();

            // Exactly the state of a database written by an older build: metacode present, no source rows.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.EnsureDatabase()}"))
            {
                connection.Open();
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM source_document;";
                delete.ExecuteNonQuery();
            }

            var again = await service.BuildAsync();

            // Not one document was complete, so none was skipped: both halves are written again.
            Assert.Equal(0, again.Skipped);
            Assert.Equal(2, again.Indexed);
            Assert.Equal(2, again.Sources);
            Assert.Equal(("cs", Counter), service.Source(workspace.PathOf("Counter.cs"))!.Value);

            // And the pass after that skips them again.
            Assert.Equal(2, (await service.BuildAsync()).Skipped);
        }
    }

    private static List<string> SyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Preview));
        return tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
    }
}
