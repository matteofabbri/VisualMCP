using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Snitch.Metacode;
using Snitch.Metacode.Coding;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Indexing;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// metacoding: decompilation that stays in the store as trees, and trees that become code in the
/// solution (rewrite a declaration, add a member) through the compiler gate.
/// </summary>
public sealed class MetacodingTests
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

    private const string SumMetacode = """
        PUBLIC STATIC METHOD Sum(n : int) : int
        RETURN n * 2
        END METHOD
        """;

    private static (FakeWorkspace Workspace, VisualMcpStore Store, MetacodeIndexService Index, MetacodeCodingService Coding) Create()
    {
        var workspace = FakeWorkspace.Create(("Counter.cs", Counter));
        var store = new VisualMcpStore(workspace);
        var index = new MetacodeIndexService(workspace, store);
        var coding = new MetacodeCodingService(
            workspace, index, new DecompileService(), new GhidraService(), new NativeCodeStore(store));
        return (workspace, store, index, coding);
    }

    // ------------------------------------------------------------------ decompilation → trees

    [Fact]
    public async Task A_decompiled_type_is_stored_as_metacode_trees_under_the_assembly_prefix()
    {
        var (workspace, store, index, _) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var assembly = typeof(MetacodeTranslator).Assembly.Location;
            var decompilation = new DecompileService()
                .DecompileTypeToMetacode(assembly, "Snitch.Metacode.MetacodeTranslator");
            Assert.Null(decompilation.Error);

            var report = await index.IndexDecompilationAsync(decompilation);

            Assert.Null(report.Error);
            Assert.Equal(1, report.Indexed);
            Assert.True(report.Declarations >= 5);

            var rows = index.Trees(symbol: "MetacodeTranslator");
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.StartsWith(MetacodeIndexStore.DecompiledPrefix, row.Path));

            // The MVID is the freshness token the caller compares against the binary.
            Assert.Equal(decompilation.Mvid, index.DecompiledAssemblyHash("Snitch.Metacode.dll"));
            Assert.Null(index.DecompiledAssemblyHash("Not.Decompiled.dll"));
        }
    }

    [Fact]
    public async Task A_solution_refresh_does_not_prune_decompiled_trees()    {
        var (workspace, store, index, _) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var decompilation = new DecompileService().DecompileTypeToMetacode(
                typeof(MetacodeTranslator).Assembly.Location, "Snitch.Metacode.MetacodeTranslator");
            await index.IndexDecompilationAsync(decompilation);

            // A build pass looks for the solution's documents and prunes what it does not find; the
            // decompiled rows are not documents of the solution and must survive it.
            await index.BuildAsync();

            Assert.NotEmpty(index.Trees(symbol: "MetacodeTranslator"));
        }
    }

    [Fact]
    public async Task Decompiling_the_same_binary_twice_is_not_repeated()
    {
        var (workspace, store, index, coding) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var assembly = typeof(MetacodeTranslator).Assembly.Location;

            var first = await coding.DecompileAsync(assembly, nameFilter: "MetacodeTranslator", maxTypes: 1);
            Assert.Null(first.Error);
            Assert.True(first.Written);

            var second = await coding.DecompileAsync(assembly, nameFilter: "MetacodeTranslator", maxTypes: 1);
            Assert.Null(second.Error);
            Assert.False(second.Written);
            Assert.Contains("already stored", second.Summary);
        }
    }

    // ------------------------------------------------------------------ tree → solution

    [Fact]
    public async Task Rewrite_previews_the_change_and_leaves_the_file_alone()
    {
        var (workspace, store, index, coding) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            await index.BuildAsync();
            var before = workspace.Read("Counter.cs");

            var result = await coding.RewriteAsync("Sum", SumMetacode, dryRun: true);

            Assert.Null(result.Error);
            Assert.False(result.Written);
            Assert.Contains("return n * 2;", result.Code);
            Assert.NotNull(result.Diff);
            Assert.Equal(before, workspace.Read("Counter.cs"));
        }
    }

    [Fact]
    public async Task Rewrite_writes_the_generated_declaration_and_refreshes_the_stored_tree()
    {
        var (workspace, store, index, coding) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            await index.BuildAsync();

            var result = await coding.RewriteAsync("Sum", SumMetacode, dryRun: false);

            Assert.Null(result.Error);
            Assert.True(result.Written);

            var after = workspace.Read("Counter.cs");
            Assert.Contains("return n * 2;", after);
            Assert.DoesNotContain("total += i;", after);
            Assert.Empty(SyntaxErrors(after));

            // The store follows the file: the next read sees the new body, not the old one.
            var row = index.Trees(symbol: "Sum", limit: 1).Single();
            Assert.Contains("RETURN n * 2", row.Text);
        }
    }

    [Fact]
    public async Task Rewrite_is_refused_when_the_generated_code_does_not_compile()
    {
        var (workspace, store, index, coding) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            await index.BuildAsync();
            var before = workspace.Read("Counter.cs");

            var result = await coding.RewriteAsync("Sum", """
                PUBLIC STATIC METHOD Sum(n : int) : int
                RETURN undefinedThing
                END METHOD
                """, dryRun: false);

            Assert.NotNull(result.Error);
            Assert.Contains("Refused", result.Error!);
            Assert.False(result.Written);
            Assert.Equal(before, workspace.Read("Counter.cs"));
        }
    }

    [Fact]
    public async Task Insert_appends_the_member_inside_the_type()
    {
        var (workspace, store, index, coding) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var result = await coding.InsertAsync("Counter", """
                PUBLIC STATIC METHOD Twice(n : int) : int
                RETURN n * 2
                END METHOD
                """, dryRun: false);

            Assert.Null(result.Error);
            Assert.True(result.Written);

            var after = workspace.Read("Counter.cs");
            Assert.Contains("int Twice(int n)", after);
            Assert.Contains("return n * 2;", after);
            Assert.Empty(SyntaxErrors(after));

            // Appended to the type, not to the file: the member sits before the closing brace.
            Assert.True(after.IndexOf("Twice", StringComparison.Ordinal) < after.LastIndexOf('}'));
            Assert.Contains("    public static int Twice(int n)", after.Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public async Task An_unknown_symbol_is_an_error_and_not_a_write()
    {
        var (workspace, store, index, coding) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            await index.BuildAsync();
            var before = workspace.Read("Counter.cs");

            var result = await coding.RewriteAsync("NoSuchMethod", SumMetacode, dryRun: false);

            Assert.NotNull(result.Error);
            Assert.Equal(before, workspace.Read("Counter.cs"));
        }
    }

    [Fact]
    public async Task The_store_lists_the_binaries_it_holds_code_for()
    {
        var (workspace, store, index, _) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var decompilation = new DecompileService().DecompileTypeToMetacode(
                typeof(MetacodeTranslator).Assembly.Location, "Snitch.Metacode.MetacodeTranslator");
            await index.IndexDecompilationAsync(decompilation);

            var entry = Assert.Single(index.DecompiledAssemblies());
            Assert.Equal("Snitch.Metacode.dll", entry.Assembly);
            Assert.Equal(decompilation.Mvid, entry.Mvid);
            Assert.Equal(1, entry.Types);
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
