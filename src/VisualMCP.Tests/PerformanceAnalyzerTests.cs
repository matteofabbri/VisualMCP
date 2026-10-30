using Snitch.Languages.CSharp.Analysis.Performance;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The three allocation anti-patterns of <c>find_performance_anti_patterns</c>, each checked on a real
/// compilation so the rule reacts to the type and not to the spelling.
/// </summary>
public sealed class PerformanceAnalyzerTests
{
    private static async Task<System.Text.Json.JsonElement> FindAsync(FakeWorkspace workspace, int maxResults = 100)
    {
        var analyzer = new PerformancePatternAnalyzer(workspace);
        var result = await analyzer.FindAntiPatternsAsync(projectName: null, maxResults);
        return ResultAssert.Json(result);
    }

    [Fact]
    public async Task String_concatenation_inside_a_loop_is_reported()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                public string Join(int n)
                {
                    var text = string.Empty;
                    for (var i = 0; i < n; i++)
                    {
                        text += i.ToString();
                    }

                    return text;
                }
            }
            """));

        var json = await FindAsync(workspace);

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        var finding = json.GetProperty("findings")[0];
        Assert.Equal("PF0001", finding.GetProperty("Rule").GetString());
        Assert.Equal("Join", finding.GetProperty("Member").GetString());
        Assert.Equal(8, finding.GetProperty("Line").GetInt32());
    }

    [Fact]
    public async Task A_linq_materialiser_inside_a_loop_is_reported()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;
            using System.Linq;

            internal class C
            {
                public int Count(IEnumerable<int> values)
                {
                    var total = 0;
                    foreach (var value in values)
                    {
                        total += values.Where(x => x > value).ToList().Count;
                    }

                    return total;
                }
            }
            """));

        var json = await FindAsync(workspace);

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        var finding = json.GetProperty("findings")[0];
        Assert.Equal("PF0002", finding.GetProperty("Rule").GetString());
        Assert.Contains("ToList", finding.GetProperty("Message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_collection_allocated_per_iteration_is_reported()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;

            internal class C
            {
                public int Sum(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        var buffer = new List<int>();
                        buffer.Add(i);
                        total += buffer.Count;
                    }

                    return total;
                }
            }
            """));

        var json = await FindAsync(workspace);

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal("PF0003", json.GetProperty("findings")[0].GetProperty("Rule").GetString());
    }

    [Fact]
    public async Task The_same_shape_outside_a_loop_is_not_reported()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;

            internal class C
            {
                public string Join(int i)
                {
                    var text = string.Empty;
                    text += i.ToString();
                    var buffer = new List<int>();
                    buffer.Add(i);
                    return text;
                }
            }
            """));

        var json = await FindAsync(workspace);

        Assert.Equal(0, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Generated_files_are_skipped()
    {
        using var workspace = FakeWorkspace.Create(("C.g.cs", """
            internal class C
            {
                public string Join(int n)
                {
                    var text = string.Empty;
                    for (var i = 0; i < n; i++)
                    {
                        text += i.ToString();
                    }

                    return text;
                }
            }
            """));

        var json = await FindAsync(workspace);

        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("projects")[0].GetProperty("filesAnalyzed").GetInt32());
    }

    [Fact]
    public async Task MaxResults_caps_the_report()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                public string Join(int n)
                {
                    var text = string.Empty;
                    for (var i = 0; i < n; i++)
                    {
                        text += i.ToString();
                        text += "!";
                    }

                    return text;
                }
            }
            """));

        var json = await FindAsync(workspace, maxResults: 1);

        Assert.Equal(1, json.GetProperty("total").GetInt32());
    }
}
