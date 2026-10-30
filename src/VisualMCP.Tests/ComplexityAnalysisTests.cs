using System.Text.Json;
using Snitch.Languages.CSharp.Analysis;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The complexity estimate is a heuristic, so these tests pin the cases the tool must get right:
/// loop nesting and bounds, LINQ composition, sorting, recursion shape, peak memory and the
/// interprocedural propagation of an in-solution callee.
/// </summary>
public sealed class ComplexityAnalysisTests
{
    private static async Task<JsonElement> AnalyzeAsync(
        string source,
        string? methodName = null,
        int topN = 50,
        string minComplexity = "O(1)")
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", source));
        var service = new AnalyzeComplexityService(workspace);
        var result = await service.AnalyzeComplexity(
            projectName: null, filePath: null, methodName: methodName, topN: topN, minComplexity: minComplexity);
        return ResultAssert.Json(result);
    }

    private static JsonElement Method(JsonElement root, string name)
    {
        var match = root.GetProperty("methods").EnumerateArray()
            .FirstOrDefault(m => m.GetProperty("Method").GetString()?
                .Contains(name + "(", StringComparison.Ordinal) == true);

        Assert.True(match.ValueKind == JsonValueKind.Object, $"method '{name}' not in the result: {root.GetRawText()}");
        return match;
    }

    private static string Time(JsonElement method) => method.GetProperty("TimeComplexity").GetString()!;
    private static string Space(JsonElement method) => method.GetProperty("SpaceComplexity").GetString()!;

    [Fact]
    public async Task Constant_method_is_O_1_in_time_and_space()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        var method = Method(root, "Add");
        Assert.Equal("O(1)", Time(method));
        Assert.Equal("O(1)", Space(method));
    }

    [Fact]
    public async Task One_loop_is_linear()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Sum(int[] values)
                {
                    var total = 0;
                    for (var i = 0; i < values.Length; i++) total += values[i];
                    return total;
                }
            }
            """);

        var method = Method(root, "Sum");
        Assert.Equal("O(n)", Time(method));
        Assert.Equal("O(1)", Space(method));
        Assert.Single(method.GetProperty("Loops").EnumerateArray());
    }

    [Fact]
    public async Task Nested_loops_are_quadratic()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Count(int[] a, int[] b)
                {
                    var count = 0;
                    for (var i = 0; i < a.Length; i++)
                        for (var j = 0; j < b.Length; j++)
                            if (a[i] == b[j]) count++;
                    return count;
                }
            }
            """);

        var method = Method(root, "Count");
        Assert.Equal("O(n^2)", Time(method));
        Assert.Equal(2, method.GetProperty("Loops").GetArrayLength());
    }

    [Fact]
    public async Task Halving_loop_is_logarithmic()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Steps(int n)
                {
                    var steps = 0;
                    for (var i = n; i > 0; i /= 2) steps++;
                    return steps;
                }
            }
            """);

        Assert.Equal("O(log n)", Time(Method(root, "Steps")));
    }

    [Fact]
    public async Task Sorting_is_linearithmic()
    {
        var root = await AnalyzeAsync("""
            using System;
            public static class Probe
            {
                public static void SortIt(int[] values) => Array.Sort(values);
            }
            """);

        Assert.Equal("O(n log n)", Time(Method(root, "SortIt")));
    }

    [Fact]
    public async Task Subtractive_recursion_is_linear_and_uses_linear_stack()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static long Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
            }
            """);

        var method = Method(root, "Fact");
        Assert.Equal("O(n)", Time(method));
        Assert.Equal("O(n)", Space(method));
        Assert.Equal("subtractive", method.GetProperty("Recursion").GetProperty("Pattern").GetString());
    }

    [Fact]
    public async Task Halving_recursion_is_logarithmic()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Bits(int n) => n == 0 ? 0 : (n & 1) + Bits(n >> 1);
            }
            """);

        var method = Method(root, "Bits");
        Assert.Equal("O(log n)", Time(method));
        Assert.Equal("O(log n)", Space(method));
        Assert.Equal("halving", method.GetProperty("Recursion").GetProperty("Pattern").GetString());
    }

    [Fact]
    public async Task Branching_recursion_is_exponential()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
            }
            """);

        var method = Method(root, "Fib");
        Assert.Equal("O(2^n)", Time(method));
        Assert.Equal("O(n)", Space(method));
        Assert.Equal("exponential", method.GetProperty("Recursion").GetProperty("Pattern").GetString());
    }

    [Fact]
    public async Task Materialising_linq_is_linear_time_and_linear_space()
    {
        var root = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Linq;
            public static class Probe
            {
                public static List<int> Positive(int[] items) => items.Where(x => x > 0).ToList();
            }
            """);

        var method = Method(root, "Positive");
        Assert.Equal("O(n)", Time(method));
        Assert.Equal("O(n)", Space(method));
    }

    [Fact]
    public async Task Lazy_linq_chain_is_one_pass_not_quadratic()
    {
        var root = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Linq;
            public static class Probe
            {
                public static IEnumerable<int> Doubled(int[] items) => items.Where(x => x > 0).Select(x => x * 2);
            }
            """);

        var method = Method(root, "Doubled");
        Assert.Equal("O(n)", Time(method));
        Assert.Equal("O(1)", Space(method));
    }

    [Fact]
    public async Task Array_sized_by_the_input_is_linear_space()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int[] Copy(int[] items)
                {
                    var copy = new int[items.Length];
                    for (var i = 0; i < items.Length; i++) copy[i] = items[i];
                    return copy;
                }
            }
            """);

        var method = Method(root, "Copy");
        Assert.Equal("O(n)", Time(method));
        Assert.Equal("O(n)", Space(method));
    }

    [Fact]
    public async Task Linear_lookup_inside_a_loop_is_quadratic_and_recommends_a_hash_set()
    {
        var root = await AnalyzeAsync("""
            using System.Collections.Generic;
            public static class Probe
            {
                public static int CountMatches(int[] items, List<int> known)
                {
                    var count = 0;
                    foreach (var item in items)
                        if (known.Contains(item)) count++;
                    return count;
                }
            }
            """);

        var method = Method(root, "CountMatches");
        Assert.Equal("O(n^2)", Time(method));
        Assert.Contains(
            method.GetProperty("Recommendations").EnumerateArray().Select(r => r.GetString()!),
            r => r.Contains("HashSet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task String_concatenation_in_a_loop_is_quadratic()
    {
        var root = await AnalyzeAsync("""
            using System;
            public static class Probe
            {
                public static string Join(int[] items)
                {
                    var text = "";
                    foreach (var item in items) text += item.ToString();
                    return text;
                }
            }
            """);

        var method = Method(root, "Join");
        Assert.Equal("O(n^2)", Time(method));
        Assert.Equal("O(n)", Space(method));
        Assert.Contains(
            method.GetProperty("Recommendations").EnumerateArray().Select(r => r.GetString()!),
            r => r.Contains("StringBuilder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complexity_of_an_in_solution_callee_is_inlined()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Total(int[] values) => Sum(values);

                public static int Sum(int[] values)
                {
                    var total = 0;
                    foreach (var value in values) total += value;
                    return total;
                }
            }
            """);

        Assert.Equal("O(n)", Time(Method(root, "Total")));
        Assert.Equal("O(n)", Time(Method(root, "Sum")));
    }

    [Fact]
    public async Task Min_complexity_filters_out_constant_methods()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Add(int a, int b) => a + b;

                public static int Sum(int[] values)
                {
                    var total = 0;
                    foreach (var value in values) total += value;
                    return total;
                }
            }
            """,
            minComplexity: "O(n)");

        Assert.DoesNotContain(
            root.GetProperty("methods").EnumerateArray(),
            m => m.GetProperty("Method").GetString()!.Contains("Add(", StringComparison.Ordinal));
        Assert.Single(root.GetProperty("methods").EnumerateArray());
        Assert.Equal(1, root.GetProperty("matchedMethods").GetInt32());
    }

    [Fact]
    public async Task Method_name_filter_selects_one_callable()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Add(int a, int b) => a + b;

                public static int Sum(int[] values)
                {
                    var total = 0;
                    foreach (var value in values) total += value;
                    return total;
                }
            }
            """,
            methodName: "Sum");

        Assert.Single(root.GetProperty("methods").EnumerateArray());
        Assert.Equal("O(n)", Time(Method(root, "Sum")));
    }

    [Fact]
    public async Task Distribution_counts_every_analysed_method()
    {
        var root = await AnalyzeAsync("""
            public static class Probe
            {
                public static int Add(int a, int b) => a + b;

                public static int Sum(int[] values)
                {
                    var total = 0;
                    foreach (var value in values) total += value;
                    return total;
                }
            }
            """);

        Assert.Equal(2, root.GetProperty("methodCount").GetInt32());
        Assert.Equal(1, root.GetProperty("distribution").GetProperty("Constant").GetInt32());
        Assert.Equal(1, root.GetProperty("distribution").GetProperty("Linear").GetInt32());
        Assert.Equal("O(n)", root.GetProperty("worstTimeComplexity").GetString());
    }
}
