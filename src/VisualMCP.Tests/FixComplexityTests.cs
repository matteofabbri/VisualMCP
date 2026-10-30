using System.Text.Json;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The one-call orchestrator behind <c>fix_complexity</c>: it must rank like the complexity report,
/// lower the worst methods with the reducer, and aggregate the runs so the caller never has to name
/// a method or read three separate tool answers.
/// </summary>
public sealed class FixComplexityTests
{
    private static FixComplexityService Service(FakeWorkspace workspace) =>
        new(workspace, new ComplexityReportService(workspace), new ReduceComplexityService(workspace));

    [Fact]
    public async Task Fix_PreviewsTheWorstMethodAndWritesNothingByDefault()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", Flattenable));

        var result = await Service(workspace).FixComplexity(target: 2, maxMethods: 3);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("changed").GetBoolean(), json.GetRawText());
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, json.GetProperty("totals").GetProperty("lowered").GetInt32());

        var method = json.GetProperty("methods")[0];
        Assert.True(method.GetProperty("applied").GetBoolean(), method.GetRawText());
        Assert.Contains("if (!(x > 0))", method.GetProperty("preview").GetString());

        // A preview must not touch the file, and it cannot know the remaining count.
        Assert.Equal(Flattenable, workspace.Read("C.cs").Replace("\r\n", "\n"));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("after").ValueKind);
    }

    [Fact]
    public async Task Fix_WritesTheFilesAndReportsWhatIsLeft()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", Extractable));

        var result = await Service(workspace).FixComplexity(target: 3, maxMethods: 3, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("changed").GetBoolean(), json.GetRawText());
        Assert.Equal(1, json.GetProperty("totals").GetProperty("lowered").GetInt32());

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("private int Extract(int[] values, int limit)", text);
        Assert.Contains("return Extract(values, limit);", text);

        // The written run re-ranks: extraction moves the decisions into a helper, so the solution's
        // worst score drops even though the helper itself may be the next thing worth lowering.
        Assert.True(
            json.GetProperty("after").GetProperty("worstCognitive").GetInt32() <=
            json.GetProperty("before").GetProperty("worstCognitive").GetInt32());
        Assert.True(
            json.GetProperty("after").GetProperty("worstCyclomatic").GetInt32() <=
            json.GetProperty("before").GetProperty("worstCyclomatic").GetInt32());
    }

    [Fact]
    public async Task Fix_LeavesASolutionThatIsAlreadySimpleAlone()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Add(int a, int b) => a + b;
            }
            """));

        var result = await Service(workspace).FixComplexity(target: 10);

        var json = ResultAssert.Json(result);
        Assert.False(json.GetProperty("changed").GetBoolean(), json.GetRawText());
        Assert.Contains("Nothing to do", json.GetProperty("summary").GetString());
        Assert.Empty(json.GetProperty("methods").EnumerateArray());
    }

    [Fact]
    public async Task Fix_ReportsTheMethodsOnlyAHumanCanRewrite()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", Ladder));

        var result = await Service(workspace).FixComplexity(target: 2, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.False(json.GetProperty("changed").GetBoolean(), json.GetRawText());
        Assert.Equal(1, json.GetProperty("totals").GetProperty("needAHuman").GetInt32());
        Assert.Contains("need a human", json.GetProperty("summary").GetString());

        var method = json.GetProperty("methods")[0];
        Assert.False(method.GetProperty("applied").GetBoolean());
        Assert.NotEmpty(method.GetProperty("manualSuggestions").EnumerateArray());

        Assert.Equal(Ladder, workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Fix_ScopesTheRunToOneFile()
    {
        using var workspace = FakeWorkspace.Create(("A.cs", Flattenable), ("B.cs", FlattenableInClass("B")));

        var result = await Service(workspace).FixComplexity(filePath: workspace.PathOf("A.cs"), target: 2, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("totals").GetProperty("considered").GetInt32());
        Assert.Equal(1, json.GetProperty("totals").GetProperty("lowered").GetInt32());

        Assert.Contains("if (!(x > 0))", workspace.Read("A.cs"));
        Assert.Equal(FlattenableInClass("B"), workspace.Read("B.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Fix_LowersTwoMethodsInTheSameFileAfterOneShiftsTheOther()
    {
        // Lowering First inserts a helper above Second, so the ranked line for Second goes stale:
        // the orchestrator has to re-find it by signature before handing it to the reducer.
        using var workspace = FakeWorkspace.Create(("C.cs", TwoComplexMethods));

        var result = await Service(workspace).FixComplexity(target: 2, maxMethods: 2, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("totals").GetProperty("considered").GetInt32());
        Assert.Equal(2, json.GetProperty("totals").GetProperty("lowered").GetInt32());
        Assert.Empty(json.GetProperty("skipped").EnumerateArray());

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("if (!(x > 0))", text);
        Assert.Contains("return Extract(values, limit);", text);
    }

    // ── Sources ─────────────────────────────────────────────────────────────────────────

    private const string Flattenable = """
        namespace Probe;

        internal class C
        {
            public void Handle(int x)
            {
                var y = x + 1;
                if (x > 0)
                {
                    for (var i = 0; i < x; i++)
                    {
                        y += i;
                    }
                    System.Console.WriteLine(y);
                }
            }
        }
        """;

    private static string FlattenableInClass(string typeName) => Flattenable.Replace("class C", "class " + typeName);

    private const string Extractable = """
        namespace Probe;

        internal class C
        {
            public int Compute(int[] values, int limit)
            {
                if (limit <= 0) return 0;
                if (limit > 10)
                {
                    var count = 0;
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (values[i] > limit) count++;
                        if (values[i] < 0) count--;
                    }
                    return count;
                }
                return limit;
            }
        }
        """;

    private const string Ladder = """
        namespace Probe;

        internal class C
        {
            public string Grade(int score)
            {
                if (score > 90) return "A";
                else if (score > 80) return "B";
                else if (score > 70) return "C";
                else if (score > 60) return "D";
                else return "F";
            }
        }
        """;

    private const string TwoComplexMethods = """
        namespace Probe;

        internal class C
        {
            public int Second(int[] values, int limit)
            {
                if (limit <= 0) return 0;
                if (limit > 10)
                {
                    var count = 0;
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (values[i] > limit) count++;
                        if (values[i] < 0) count--;
                    }
                    return count;
                }
                return limit;
            }

            public void First(int x)
            {
                var y = x + 1;
                if (x > 0)
                {
                    for (var i = 0; i < x; i++)
                    {
                        y += i;
                    }
                    System.Console.WriteLine(y);
                }
            }
        }
        """;
}
