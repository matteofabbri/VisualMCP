using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The analysis and generation tools that used to read C# syntax only, run over a Visual Basic
/// project: the metric ranking, LCOM4, clone detection, the Big-O walk, member ordering, the
/// <c>'''</c> documentation comments, the interface stubs, and the two reducer moves VB can apply.
/// </summary>
public sealed class VbAnalysisTests
{
    private const string Calculator = """
        Friend Class Calculator
            Private _seed As Integer = 1
            Private _last As Integer = 0

            Public Function Add(x As Integer) As Integer
                _last = _seed + x
                Return _last
            End Function

            Public Function Total(items As System.Collections.Generic.List(Of Integer)) As Integer
                Dim total As Integer = 0
                For Each item As Integer In items
                    For i As Integer = 0 To items.Count - 1
                        total += item
                    Next
                Next
                Return total
            End Function

            Public Function Factorial(n As Integer) As Integer
                If n <= 1 Then Return 1
                Return n * Factorial(n - 1)
            End Function
        End Class
        """;

    [Fact]
    public async Task GetMetrics_RanksVisualBasicMethods()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", Calculator);
        var service = new GetMetricsService(workspace);

        var json = ResultAssert.Json(await service.GetMetrics(null, 1));

        Assert.Equal(3, json.GetProperty("methodCount").GetInt32());
        Assert.All(json.GetProperty("metrics").EnumerateArray(), entry =>
            Assert.Equal("Calculator.vb", Path.GetFileName(entry.GetProperty("FilePath").GetString())));
        // Worst first: Total loops twice, so it owns the highest cognitive score.
        Assert.Contains("Total", json.GetProperty("metrics")[0].GetProperty("FullName").GetString());
    }

    [Fact]
    public async Task GetCohesionMetrics_FlagsTwoFieldClusters()
    {
        const string source = """
            Friend Class Split
                Private _left As Integer
                Private _right As Integer

                Public Function ReadLeft() As Integer
                    Return _left
                End Function

                Public Sub WriteLeft(value As Integer)
                    _left = value
                End Sub

                Public Function ReadRight() As Integer
                    Return _right
                End Function

                Public Sub WriteRight(value As Integer)
                    _right = value
                End Sub
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Split.vb", source);
        var service = new GetCohesionMetricsService(workspace);

        var json = ResultAssert.Json(await service.Measure(null, 2, 10));

        Assert.True(json.GetProperty("typeCount").GetInt32() == 1, json.GetRawText());
        Assert.True(json.GetProperty("flaggedCount").GetInt32() == 1, json.GetRawText());
        Assert.Equal(2, json.GetProperty("classes")[0].GetProperty("components").GetInt32());
    }

    /// <summary>The entry of a method in the analyze_complexity list, matched on its display signature.</summary>
    private static System.Text.Json.JsonElement Entry(System.Text.Json.JsonElement json, string method) =>
        json.GetProperty("methods").EnumerateArray()
            .Single(entry => entry.GetProperty("FullName").GetString()!
                .Contains(method + "(", StringComparison.Ordinal));

    [Fact]
    public async Task FindDuplicateCode_MatchesTwoVisualBasicBodies()
    {
        const string source = """
            Friend Class Copies
                Public Sub First(items As System.Collections.Generic.List(Of Integer))
                    Dim total As Integer = 0
                    For Each item As Integer In items
                        total += item
                        System.Console.WriteLine(item)
                    Next
                End Sub

                Public Sub Second(values As System.Collections.Generic.List(Of Integer))
                    Dim sum As Integer = 0
                    For Each value As Integer In values
                        sum += value
                        System.Console.WriteLine(value)
                    Next
                End Sub
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Copies.vb", source);
        var service = new FindDuplicateCodeService(workspace);

        var json = ResultAssert.Json(await service.FindDuplicateCode(null, 2, 6));

        Assert.True(json.GetProperty("totalClusters").GetInt32() >= 1, json.GetRawText());
        Assert.Contains(json.GetProperty("cloneClusters").EnumerateArray(), cluster =>
            cluster.GetProperty("Occurrences").EnumerateArray()
                .Select(occurrence => occurrence.GetProperty("Line").GetInt32())
                .Distinct()
                .Count() >= 2);
    }

    [Fact]
    public async Task AnalyzeComplexity_SeesTheNestedLoopAndTheRecursion()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", Calculator);
        var service = new AnalyzeComplexityService(workspace);

        var json = ResultAssert.Json(await service.AnalyzeComplexity(null, null, null, 25, "O(n)"));

        var total = Entry(json, "Total");
        var factorial = Entry(json, "Factorial");

        Assert.Equal("O(n^2)", total.GetProperty("TimeComplexity").GetString());
        Assert.Equal("O(n)", factorial.GetProperty("TimeComplexity").GetString());
        Assert.Equal("subtractive", factorial.GetProperty("Recursion").GetProperty("Pattern").GetString());
        // The single field write in Add does not grow with the input.
        Assert.DoesNotContain(json.GetProperty("methods").EnumerateArray(),
            entry => entry.GetProperty("FullName").GetString()!.Contains(".Add("));
    }

    [Fact]
    public async Task ReorderMembers_PutsVisualBasicFieldsBeforeMethods()
    {
        const string source = """
            Friend Class Order
                Public Sub Zebra()
                End Sub

                Public Sub Alpha()
                End Sub

                Public Counter As Integer
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Order.vb", source);
        var service = new ReorderMembersService(workspace);

        var json = ResultAssert.Json(await service.ReorderMembers(null, workspace.PathOf("Order.vb"), dryRun: false));
        Assert.Equal(1, json.GetProperty("filesModified").GetInt32());

        var text = workspace.Read("Order.vb");
        Assert.True(text.IndexOf("Public Counter", StringComparison.Ordinal) <
                    text.IndexOf("Public Sub Alpha", StringComparison.Ordinal), text);
        Assert.True(text.IndexOf("Public Sub Alpha", StringComparison.Ordinal) <
                    text.IndexOf("Public Sub Zebra", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task GenerateDocs_WritesVisualBasicDocumentationComments()
    {
        const string source = """
            Friend Class Docs
                Public Function CalculateTotal(amount As Integer) As Integer
                    Return amount
                End Function
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Docs.vb", source);
        var service = new GenerateDocsService(workspace);

        var json = ResultAssert.Json(await service.GenerateDocs(
            workspace.PathOf("Docs.vb"), "CalculateTotal", includeMembers: false, dryRun: false));

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        var text = workspace.Read("Docs.vb");
        Assert.Contains("''' <summary>", text);
        Assert.Contains("''' <param name=\"amount\">", text);
        Assert.Contains("''' <returns>", text);
        Assert.DoesNotContain("///", text);
    }

    [Fact]
    public async Task ImplementMembers_GeneratesVisualBasicStubsThatSatisfyTheInterface()
    {
        const string source = """
            Friend Interface IGreeter
                Sub Greet(name As String)
                Function Count() As Integer
                ReadOnly Property Name As String
            End Interface

            Friend Class Greeter
                Implements IGreeter
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Greeter.vb", source);
        var service = new ImplementMembersService(workspace);

        var json = ResultAssert.Json(await service.ImplementMembers(
            workspace.PathOf("Greeter.vb"), "Greeter", dryRun: false));

        Assert.Equal(3, json.GetProperty("stubCount").GetInt32());
        Assert.Equal(0, json.GetProperty("missingMemberErrors").GetProperty("after").GetInt32());

        var text = workspace.Read("Greeter.vb");
        Assert.Contains("Implements IGreeter.Greet", text);
        Assert.Contains("Implements IGreeter.Count", text);
        Assert.Contains("Implements IGreeter.Name", text);
        Assert.Contains("Throw New System.NotImplementedException()", text);
    }

    [Fact]
    public async Task ReduceComplexity_FlattensTheTailIfOfAVisualBasicSub()
    {
        const string source = """
            Friend Class Guard
                Public Sub Run(flag As Boolean, count As Integer)
                    System.Console.WriteLine("start")
                    If flag AndAlso count > 0 Then
                        If count > 10 Then
                            System.Console.WriteLine("many")
                        End If
                    End If
                End Sub
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Guard.vb", source);
        var service = new ReduceComplexityService(workspace);

        var json = ResultAssert.Json(await service.ReduceComplexity(
            workspace.PathOf("Guard.vb"), methodName: "Run", target: 1, dryRun: true));

        Assert.True(json.GetProperty("applied").GetBoolean(), json.GetRawText());
        Assert.Contains("If Not (", json.GetProperty("preview").GetString());
        Assert.True(
            json.GetProperty("after").GetProperty("Cognitive").GetInt32() <
            json.GetProperty("before").GetProperty("Cognitive").GetInt32(),
            json.GetRawText());
    }

    [Fact]
    public async Task ReduceComplexity_NamesALongVisualBasicCondition()
    {
        const string source = """
            Friend Class Conditions
                Public Function Check(a As Boolean, b As Boolean, c As Boolean, d As Boolean) As Boolean
                    If a AndAlso b AndAlso c AndAlso d Then
                        Return True
                    End If
                    Return False
                End Function
            End Class
            """;

        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Conditions.vb", source);
        var service = new ReduceComplexityService(workspace);

        var json = ResultAssert.Json(await service.ReduceComplexity(
            workspace.PathOf("Conditions.vb"), methodName: "Check", target: 1, dryRun: true));

        Assert.True(json.GetProperty("applied").GetBoolean(),
            json.GetProperty("suggestions").GetRawText() + " | " + json.GetProperty("note").GetString());
        var preview = json.GetProperty("preview").GetString()!;
        Assert.Contains("Private Function IsCheckCondition(", preview);
        Assert.Contains("As Boolean", preview);
    }

    [Fact]
    public void ComplexityPlanner_ReportsTheVisualBasicExtractionAsManual()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", Calculator);
        var document = workspace.CurrentSolution!.Projects.First().Documents.First();
        var model = document.GetSemanticModelAsync().GetAwaiter().GetResult();
        var root = document.GetSyntaxRootAsync().GetAwaiter().GetResult();

        var unit = ComplexityAnalyzer.MethodUnits(root!).Single(node => ComplexityAnalyzer.NameOf(node) == "Total");
        var suggestions = ComplexityPlanner.Suggest(unit, model!);

        var extraction = Assert.Single(suggestions, suggestion => suggestion.Kind == "extract-method");
        Assert.False(extraction.AutoApplicable);
        Assert.Contains("hand edit", extraction.BlockedBy!);
    }
}
