using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The cyclomatic/cognitive metric engine, the report built on it and the reducer that lowers the
/// score: the numbers a maintainability review reads, and the refactorings that move them.
/// </summary>
public sealed class ComplexityTests
{
    // ── The metrics ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cyclomatic_CountsEveryDecisionPoint()
    {
        var metrics = Analyze("""
            internal class C
            {
                public void Mixed(int x)
                {
                    if (x > 0) { }
                    for (var i = 0; i < x; i++) { }
                    switch (x) { case 1: break; default: break; }
                    try { } catch { }
                    var y = x > 0 ? 1 : 2;
                    System.Console.WriteLine(y);
                }
            }
            """, "Mixed");

        // 1 base + if + for + (two switch paths = 1 extra) + catch + ternary
        Assert.Equal(6, metrics.Cyclomatic);

        // Every one of them sits at nesting 0, so each costs exactly 1 to a reader.
        Assert.Equal(5, metrics.Cognitive);
        Assert.Equal(0, metrics.MaxNesting);
    }

    [Fact]
    public void Cognitive_ChargesOnePerLogicalRun_NotOnePerOperator()
    {
        Assert.Equal(3, Analyze("""
            internal class C
            {
                public bool All(bool a, bool b, bool c) => a && b && c;
            }
            """, "All").Cyclomatic);

        // `a && b && c` is one decision to a reader; `(a && b) || c` changes operator, so it is two.
        Assert.Equal(1, Analyze("""
            internal class C
            {
                public bool All(bool a, bool b, bool c) => a && b && c;
            }
            """, "All").Cognitive);

        Assert.Equal(2, Analyze("""
            internal class C
            {
                public bool Any(bool a, bool b, bool c) => (a && b) || c;
            }
            """, "Any").Cognitive);
    }

    [Fact]
    public void Cognitive_ChargesNesting_NotJustTheCount()
    {
        var metrics = Analyze("""
            internal class C
            {
                public void Nested(int x)
                {
                    if (x > 0)
                    {
                        if (x > 1)
                        {
                            for (var i = 0; i < x; i++) { }
                        }
                    }
                }
            }
            """, "Nested");

        Assert.Equal(4, metrics.Cyclomatic);   // 1 + if + if + for
        Assert.Equal(6, metrics.Cognitive);    // 1 + 2 + 3: the same three decisions, three levels deep
        Assert.Equal(2, metrics.MaxNesting);
    }

    [Fact]
    public void NestedFunction_IsItsOwnUnit_NotPartOfTheMethodThatDeclaresIt()
    {
        var metrics = Analyze("""
            internal class C
            {
                public void WithLambda()
                {
                    System.Action a = () => { if (true) { } };
                    if (true) { }
                }
            }
            """, "WithLambda");

        // Only the outer if: the lambda's if belongs to the lambda.
        Assert.Equal(2, metrics.Cyclomatic);
    }

    [Fact]
    public void SwitchExpression_CostsOnePerExtraPath()
    {
        var metrics = Analyze("""
            internal class C
            {
                public int Map(int x) => x switch { 1 => 1, 2 => 2, _ => 0 };
            }
            """, "Map");

        Assert.Equal(3, metrics.Cyclomatic);   // 1 + two extra arms
        Assert.Equal(1, metrics.Cognitive);    // a switch reads as a table, not a staircase
    }

    [Fact]
    public void Contributions_PointAtTheLinesThatOwnTheScore()
    {
        var metrics = Analyze("""
            internal class C
            {
                public void Nested(int x)
                {
                    if (x > 0)
                    {
                        if (x > 1)
                        {
                            for (var i = 0; i < x; i++) { }
                        }
                    }
                }
            }
            """, "Nested");

        Assert.Equal(3, metrics.Contributions.Count);
        Assert.Contains(metrics.Contributions, contribution => contribution.Kind == "if" && contribution.Line == 5);
        Assert.Contains(metrics.Contributions, contribution => contribution.Kind == "for" && contribution.Nesting == 2);

        // Ranked by what they cost, the deepest loop is the first thing to fix.
        Assert.Equal("for", metrics.WorstContributions(1).Single().Kind);
    }

    [Fact]
    public void MaintainabilityIndex_IsAFractionOfHundred()
    {
        var metrics = Analyze("""
            internal class C
            {
                public int F(int a, int b) => a + b;
            }
            """, "F");

        Assert.InRange(metrics.MaintainabilityIndex, 0, 100);
        Assert.True(metrics.HalsteadVolume > 0);
    }

    // ── The report ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ForOneMethod_ListsContributorsAndSuggestions()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Handle(int[] values, int limit)
                {
                    var total = 0;
                    if (limit > 0)
                    {
                        for (var i = 0; i < values.Length; i++)
                        {
                            if (values[i] > limit) total++;
                            if (values[i] < 0) total--;
                        }
                    }
                    System.Console.WriteLine(total);
                }
            }
            """));

        var service = new ComplexityReportService(workspace);
        var result = await service.ComplexityReport(
            workspace.PathOf("C.cs"), line: 5, methodName: null, projectName: null,
            topN: 20, minComplexity: 5, includeContributors: true);

        var json = ResultAssert.Json(result);
        Assert.Equal("Handle", json.GetProperty("Name").GetString());
        Assert.Equal(5, json.GetProperty("Cyclomatic").GetInt32());
        Assert.True(json.GetProperty("Cognitive").GetInt32() > 0);
        Assert.NotEmpty(json.GetProperty("Contributors").EnumerateArray());
        Assert.NotEmpty(json.GetProperty("Suggestions").EnumerateArray());
    }

    [Fact]
    public async Task Report_WithoutAMethod_SaysWhichOnesExist()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void One(int x) { if (x > 0) { } }
                public void Two(int x) { while (x > 0) { x--; } }
            }
            """));

        var service = new ComplexityReportService(workspace);
        var result = await service.ComplexityReport(
            workspace.PathOf("C.cs"), line: null, methodName: null, projectName: null,
            topN: 20, minComplexity: 5, includeContributors: false);

        Assert.Contains("which method", ResultAssert.Error(result));
        Assert.Equal(2, ResultAssert.Json(result).GetProperty("units").GetArrayLength());
    }

    // ── The reducer ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reduce_FlattensATailIfIntoAGuardClause()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
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
            """));

        var service = new ReduceComplexityService(workspace);
        var result = await service.ReduceComplexity(
            workspace.PathOf("C.cs"), line: 5, methodName: null, target: 2, maxSteps: 6, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("applied").GetBoolean(), json.GetRawText());

        // The for-loop moved out of the if, so it stopped costing a nesting level.
        Assert.Equal(2, json.GetProperty("after").GetProperty("Cognitive").GetInt32());

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("if (!(x > 0))", text);
        Assert.Contains("return;", text);

        // The body moved out of the if, so it lost one level of indentation instead of keeping it.
        var forLine = text.Split('\n').First(line => line.Contains("for (var i"));
        Assert.Equal("        for (var i = 0; i < x; i++)", forLine);
    }

    [Fact]
    public async Task Reduce_ExtractsANestedBlockIntoItsOwnMethod()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
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
            """));

        var service = new ReduceComplexityService(workspace);
        var result = await service.ReduceComplexity(
            workspace.PathOf("C.cs"), line: 5, methodName: null, target: 3, maxSteps: 6, dryRun: false);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("applied").GetBoolean(), json.GetRawText());
        Assert.Equal(3, json.GetProperty("after").GetProperty("Cyclomatic").GetInt32());

        var text = workspace.Read("C.cs");
        Assert.Contains("private int Extract(int[] values, int limit)", text);
        Assert.Contains("return Extract(values, limit);", text);
    }

    [Fact]
    public async Task Reduce_PreviewsByDefault()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public void Handle(int x)
                {
                    var y = x + 1;
                    if (x > 0)
                    {
                        for (var i = 0; i < x; i++) System.Console.WriteLine(i);
                        System.Console.WriteLine(y);
                    }
                }
            }
            """;

        using var workspace = FakeWorkspace.Create(("C.cs", source));

        var service = new ReduceComplexityService(workspace);
        var result = await service.ReduceComplexity(workspace.PathOf("C.cs"), line: 5, target: 2);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("applied").GetBoolean(), json.GetRawText());
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Contains("if (!(x > 0))", json.GetProperty("preview").GetString());

        Assert.Equal(source, workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Reduce_LeavesAMethodAlreadyUnderTheTargetAlone()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Add(int a, int b) => a + b;
            }
            """));

        var service = new ReduceComplexityService(workspace);
        var result = await service.ReduceComplexity(workspace.PathOf("C.cs"), line: 5, target: 10);

        var json = ResultAssert.Json(result);
        Assert.False(json.GetProperty("applied").GetBoolean());
        Assert.Contains("Already at or below the target", json.GetProperty("note").GetString());
    }

    private static ComplexityMetrics Analyze(string source, string methodName)
    {
        var root = CSharpSyntaxTree
            .ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))
            .GetRoot();

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(candidate => candidate.Identifier.ValueText == methodName);

        return ComplexityAnalyzer.Analyze(method);
    }
}
