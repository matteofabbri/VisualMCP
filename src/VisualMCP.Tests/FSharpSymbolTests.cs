using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.FSharp;
using Snitch.Workspace;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// fsharp_symbols: the lexical name table F# gets instead of a Roslyn model. The cases are what an
/// indentation-aware scan can prove — module, type, union cases, record fields, let bindings and members —
/// plus the one cross-file link F# writes down, an <c>open</c>.
/// </summary>
public sealed class FSharpSymbolTests
{
    private const string Source = """
        module Shop.Order

        type Status =
            | Draft
            | Paid

        type Order =
            { Id: string
              Total: decimal }

        let isPaid status =
            match status with
            | Paid -> true
            | Draft -> false

        let describe status = isPaid status

        let private roundingAmount = 0.01m
        """;

    [Fact]
    public void A_module_declares_its_types_union_cases_record_fields_and_bindings()
    {
        var symbols = FSharpSymbols.Scan(Source, "Order.fs");
        var names = symbols.Declarations.Select(declaration => declaration.Name).ToList();

        Assert.Equal(new[] { "Shop.Order" }, symbols.Modules);
        Assert.Contains("Status", names);
        Assert.Contains("Draft", names);
        Assert.Contains("Paid", names);
        Assert.Contains("Order", names);
        Assert.Contains("Id", names);
        Assert.Contains("Total", names);
        Assert.Contains("isPaid", names);
        Assert.Contains("roundingAmount", names);

        Assert.Equal(FSharpSymbolKind.Module,
            Assert.Single(symbols.Declarations, declaration => declaration.Name == "Shop.Order").Kind);
        Assert.Equal(FSharpSymbolKind.UnionCase,
            Assert.Single(symbols.Declarations, declaration => declaration.Name == "Draft").Kind);
        Assert.Equal(FSharpSymbolKind.Field,
            Assert.Single(symbols.Declarations, declaration => declaration.Name == "Id").Kind);
        Assert.Equal(FSharpSymbolKind.Function,
            Assert.Single(symbols.Declarations, declaration => declaration.Name == "isPaid").Kind);
    }

    [Fact]
    public void A_name_resolves_to_the_declaration_before_it_in_its_file()
    {
        var symbols = FSharpSymbols.Scan(Source, "Order.fs");
        var paid = symbols.Declarations.Single(declaration => declaration.Name == "Paid" && declaration.Kind == FSharpSymbolKind.UnionCase);
        var use = Assert.Single(symbols.Uses, occurrence => occurrence.Name == "Paid");
        Assert.Equal(IndexOf(symbols.Declarations, paid), use.Declaration);
    }

    [Fact]
    public void A_string_and_a_comment_are_not_names()
    {
        const string source = """
            /// <summary>Total is documented here.</summary>
            let describe total =
                let label = "Total"
                label
            """;

        var symbols = FSharpSymbols.Scan(source, "Describe.fs");
        Assert.DoesNotContain(symbols.Declarations, declaration => declaration.Name == "Total");
        Assert.DoesNotContain(symbols.Uses, occurrence => occurrence.Name == "Total");
    }

    [Fact]
    public async Task An_open_links_a_use_to_the_declaring_module()
    {
        using var files = new Scripts(
            ("Order.fs", """
                module Shop.Order

                let totalOf amount = amount * 2m
                """),
            ("Program.fs", """
                module Shop.Program

                open Shop.Order

                let run () = totalOf 3m
                """));

        var refs = Json(await Service.SymbolsAsync("refs", "totalOf", null, files.Root, false, 100));
        var symbol = Assert.Single(refs.GetProperty("symbols").EnumerateArray());
        Assert.Equal("Order.fs", symbol.GetProperty("file").GetString());

        var external = Assert.Single(symbol.GetProperty("references").EnumerateArray(),
            reference => reference.GetProperty("file").GetString() == "Program.fs");
        Assert.Contains("open Shop.Order", external.GetProperty("via").GetString());
    }

    [Fact]
    public async Task Unused_reports_a_private_binding_nothing_reads()
    {
        using var files = new Scripts(("Order.fs", Source));

        var unused = Json(await Service.SymbolsAsync("unused", null, null, files.Root, false, 100));
        var names = unused.GetProperty("candidates").EnumerateArray()
            .Select(candidate => candidate.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("roundingAmount", names);
        Assert.Contains("describe", names);
        Assert.DoesNotContain("isPaid", names);
        Assert.DoesNotContain("Draft", names); // a union case is a shape, not a value
    }

    [Fact]
    public async Task The_outline_and_the_missing_name_are_answers_with_a_reason()
    {
        using var files = new Scripts(("Order.fs", Source));

        var outline = Json(await Service.SymbolsAsync("outline", null, "Order.fs", files.Root, false, 100));
        Assert.Equal("Order.fs", outline.GetProperty("file").GetString());
        Assert.Equal("Shop.Order", Assert.Single(outline.GetProperty("modules").EnumerateArray()).GetString());

        var missing = Json(await Service.SymbolsAsync("refs", "nope", null, files.Root, false, 100));
        Assert.Contains("No declaration named 'nope'", missing.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Rename_rewrites_the_declaration_and_a_use_in_an_opening_file()
    {
        using var files = new Scripts(
            ("Order.fs", """
                module Shop.Order

                let totalOf amount = amount * 2m
                """),
            ("Program.fs", """
                module Shop.Program

                open Shop.Order

                let run () = totalOf 3m
                """));

        var result = await Service.SymbolsAsync("rename", "totalOf", null, files.Root, false, 100,
            newName: "sumOf", dryRun: false);
        var json = Json(result);

        Assert.Equal("rename", json.GetProperty("action").GetString());
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.Contains("let sumOf amount", File.ReadAllText(Path.Combine(files.Root, "Order.fs")));
        Assert.Contains("sumOf 3m", File.ReadAllText(Path.Combine(files.Root, "Program.fs")));
    }

    private static FSharpSymbolService Service => new(new NullWorkspace());

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result);

    private static int IndexOf(IReadOnlyList<FSharpSymbolDeclaration> declarations, FSharpSymbolDeclaration declaration)
    {
        for (var index = 0; index < declarations.Count; index++)
            if (ReferenceEquals(declarations[index], declaration)) return index;
        return -1;
    }

    private sealed class Scripts : IDisposable
    {
        public Scripts(params (string Name, string Source)[] files)
        {
            Root = Path.Combine(Path.GetTempPath(), "vmcp-fsharp-symbols-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var (name, source) in files)
                File.WriteAllText(Path.Combine(Root, name), source);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class NullWorkspace : IRoslynWorkspace
    {
        public Solution? CurrentSolution => null;
        public string? LoadedSolutionPath => null;

        public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) =>
            Task.FromResult<Solution?>(null);

        public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? DiscoverSolutionPath() => null;

        public bool TryUpdateDocument(DocumentId documentId, SourceText text) => false;

        public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text) => null;

        public bool TryRemoveDocument(DocumentId documentId) => false;

        public void Dispose() { }
    }
}
