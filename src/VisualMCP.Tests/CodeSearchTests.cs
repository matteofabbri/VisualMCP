using Snitch.Languages.CSharp.Search;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// search_code: the hit is the declaration, not the line. That is the whole point of the tool — it
/// replaces search_text followed by read_file, so what comes back must already be the code to read.
/// </summary>
public sealed class CodeSearchTests
{
    private const string Counter = """
        namespace Demo;

        public static class Counter
        {
            /// <summary>Adds the even numbers below n.</summary>
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

    [Fact]
    public async Task A_hit_comes_back_as_the_whole_declaration_that_contains_it()
    {
        using var workspace = FakeWorkspace.Create(("Counter.cs", Counter));
        var search = new CodeSearchService(workspace);

        var result = await search.RunAsync("total", workspace.RootDirectory, "*.cs", false, 50, 60_000, 8);

        Assert.Null(result.Error);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("method", hit.Kind);
        Assert.Equal("Demo.Counter", hit.Container);
        Assert.Contains("Sum(int n)", hit.Signature);
        Assert.Contains("return total;", hit.Source);

        // The doc comment above the declaration is part of the answer: it is not a separate read_file.
        Assert.Contains("/// <summary>", hit.Source);

        // Three matches, one declaration: the method is printed once with all its line numbers.
        Assert.True(hit.Matches.Count >= 3, $"expected several match lines, got {hit.Matches.Count}");
        Assert.Equal(1, result.DeclarationCount);
    }

    [Fact]
    public async Task A_file_outside_the_solution_comes_back_as_a_window_of_lines()
    {
        using var workspace = FakeWorkspace.Create(("Counter.cs", Counter));
        var log = Path.Combine(workspace.RootDirectory, "build.log");
        await File.WriteAllLinesAsync(log,
        [
            "line one", "line two", "ERROR CS0001 something broke", "line four", "line five",
        ]);

        var search = new CodeSearchService(workspace);
        var result = await search.RunAsync("ERROR CS", workspace.RootDirectory, "*.log", false, 10, 60_000, 1);

        Assert.Null(result.Error);
        var hit = Assert.Single(result.Hits);
        Assert.Null(hit.Kind);
        Assert.Contains("line two", hit.Source);
        Assert.Contains("ERROR CS0001 something broke", hit.Source);
        Assert.Contains("line four", hit.Source);
    }

    [Fact]
    public async Task The_budget_leaves_hits_out_instead_of_truncating_the_answer_silently()
    {
        // Two methods of about 3 KB each, both matching: the first eats the whole 2000-character floor
        // of the budget, so the second has to be named as left out rather than silently dropped.
        var filler = string.Join("\n", Enumerable.Range(0, 160).Select(i => $"        total += {i};"));
        var source = $$"""
            namespace Demo;

            public static class Big
            {
                public static int First(int n)
                {
                    var total = 0;
            {{filler}}
                    return total;
                }

                public static int Second(int n)
                {
                    var total = 0;
            {{filler}}
                    return total;
                }
            }
            """;

        using var workspace = FakeWorkspace.Create(("Big.cs", source));
        var search = new CodeSearchService(workspace);

        // Two matches, one per method: the hit is the whole (large) method, not the matched line.
        var result = await search.RunAsync(
            "public static int (First|Second)", workspace.RootDirectory, "*.cs", false, 50, 2_000, 2);

        Assert.Null(result.Error);
        Assert.NotEmpty(result.LeftOut);
        Assert.True(result.Characters <= 2_200, $"budget exceeded: {result.Characters}");
    }

    [Fact]
    public async Task An_invalid_pattern_is_an_error_and_not_an_exception()
    {
        using var workspace = FakeWorkspace.Create(("Counter.cs", Counter));
        var search = new CodeSearchService(workspace);

        var result = await search.RunAsync("([", workspace.RootDirectory, "*.cs", false, 10, 60_000, 2);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Hits);
    }
}
