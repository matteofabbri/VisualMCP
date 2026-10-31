using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Languages.CSharp.Analysis.Performance;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Metacode;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The analysis tools over Visual Basic: dead code, code smells, async and allocation anti-patterns,
/// the complexity numbers and the inspection packs — each read from VB syntax with the same report
/// shape the C# tests assert, and the inspection packs reporting which languages they actually cover.
/// </summary>
public sealed class VisualBasicAnalysisTests
{
    // ── dead code ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeadCode_FindsStatementsAfterReturnAndAConstantCondition()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Compute() As Integer
                    Return 1
                    Dim unused = 2
                End Function

                Public Function Choose() As Integer
                    If False Then
                        Return 1
                    End If
                    Return 2
                End Function
            End Class
            """);

        var result = ResultAssert.Json(await new FindDeadCodeService(workspace).FindDeadCode());

        var kinds = result.GetProperty("byKind").EnumerateObject().Select(property => property.Name).ToList();
        Assert.Contains("statement after return", kinds);
        Assert.Contains("always-false if condition", kinds);
    }

    [Fact]
    public async Task DeadCode_FindsADeadSelectCaseArmWithAConstantSelector()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Label() As String
                    Select Case 3
                        Case 1
                            Return "one"
                        Case Else
                            Return "other"
                    End Select
                End Function
            End Class
            """);

        var result = ResultAssert.Json(await new FindDeadCodeService(workspace).FindDeadCode());

        var finding = Assert.Single(result.GetProperty("findings").EnumerateArray());
        Assert.Equal("dead switch arm", finding.GetProperty("Kind").GetString());
        Assert.Equal(4, finding.GetProperty("Line").GetInt32());
    }

    // ── code smells ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CodeSmells_FindsAsyncSubAnEmptyCatchAndAwaitInSyncLock()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Private _gate As New Object()

                Public Async Sub FireAndForget()
                End Sub

                Public Sub Ignore()
                    Try
                        Throw New Exception("x")
                    Catch ex As Exception
                    End Try
                End Sub

                Public Async Function Wait() As Task
                    SyncLock _gate
                        Await Task.Delay(1)
                    End SyncLock
                    Await Task.CompletedTask
                End Function
            End Class
            """);

        var result = ResultAssert.Json(await new FindCodeSmellsService(workspace).FindCodeSmells());

        var kinds = result.GetProperty("byKind").EnumerateObject().Select(property => property.Name).ToList();
        Assert.Contains("async void", kinds);
        Assert.Contains("empty catch", kinds);
        Assert.Contains("await in lock", kinds);
    }

    // ── async anti-patterns ──────────────────────────────────────────────────

    [Fact]
    public async Task AsyncAntiPatterns_FindsResultBlockingAndAwaitInALoop()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Blocking(task As System.Threading.Tasks.Task(Of Integer)) As Integer
                    Return task.Result
                End Function

                Public Async Function Loop(tasks As System.Threading.Tasks.Task(Of Integer)()) As System.Threading.Tasks.Task
                    For i = 0 To tasks.Length - 1
                        Await tasks(i)
                    Next
                End Function
            End Class
            """);

        var result = ResultAssert.Json(await new FindAsyncAntiPatternsService(workspace).FindAsyncAntiPatterns());

        var kinds = result.GetProperty("byKind").EnumerateObject().Select(property => property.Name).ToList();
        Assert.Contains(".Result blocking call", kinds);
        Assert.Contains("await in for loop", kinds);
    }

    // ── performance ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Performance_FindsStringAppendingAndAllocationsInALoop()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Join(values As String()) As String
                    Dim text = ""
                    For Each value In values
                        text &= value
                    Next
                    Return text
                End Function

                Public Function Build(count As Integer) As Integer
                    Dim total = 0
                    For i = 0 To count - 1
                        Dim buffer = New List(Of Integer)()
                        Dim array = New Integer(4) {}
                        total += buffer.Count + array.Length
                    Next
                    Return total
                End Function
            End Class
            """);

        var result = ResultAssert.Json(await new PerformancePatternAnalyzer(workspace).FindAntiPatternsAsync(null, 50));

        var rules = result.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("Rule").GetString())
            .ToList();

        Assert.Contains("PF0001", rules);   // text &= value
        Assert.Equal(2, rules.Count(rule => rule == "PF0003"));   // the List and the array
    }

    // ── complexity ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Complexity_ScoresAVbMethodAndItsDecisionPoints()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Score(value As Integer) As Integer
                    Dim total = 0
                    If value > 0 Then
                        For i = 0 To value
                            If i Mod 2 = 0 AndAlso i > 0 Then
                                total += i
                            End If
                        Next
                    ElseIf value < 0 Then
                        total = -1
                    Else
                        total = 0
                    End If
                    Return total
                End Function
            End Class
            """);

        var solution = await workspace.EnsureSolutionLoadedAsync();
        var document = solution!.Projects.Single().Documents.Single();
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        var unit = Assert.Single(ComplexityAnalyzer.MethodUnits(root!));
        var metrics = ComplexityAnalyzer.Analyze(unit, model, document.FilePath);

        Assert.Equal("Score", metrics.Name);
        Assert.True(metrics.Cyclomatic >= 6, $"cyclomatic was {metrics.Cyclomatic}");
        Assert.True(metrics.Cognitive >= 5, $"cognitive was {metrics.Cognitive}");
        Assert.Contains(metrics.Contributions, contribution => contribution.Kind == "for");
        Assert.Contains(metrics.Contributions, contribution => contribution.Kind == "else-if");
        Assert.Contains(metrics.Contributions, contribution => contribution.Kind == "AndAlso");

        var hotspots = ResultAssert.Json(await new GetComplexityHotspotsService(workspace).GetComplexityHotspots(minComplexity: 1));
        var hotspot = Assert.Single(hotspots.GetProperty("hotspots").EnumerateArray());
        Assert.Contains("Score", hotspot.GetProperty("Method").GetString());
    }

    [Fact]
    public async Task ComplexityReport_DescribesOneVbMethod()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Score(value As Integer) As Integer
                    If value > 0 Then
                        Return 1
                    End If
                    Return 0
                End Function
            End Class
            """);

        var result = ResultAssert.Json(await new ComplexityReportService(workspace).ComplexityReport(
            filePath: workspace.PathOf("C.vb"), methodName: "Score"));

        Assert.Equal("Score", result.GetProperty("Name").GetString());
        Assert.Equal(2, result.GetProperty("Cyclomatic").GetInt32());
        Assert.Empty(result.GetProperty("Suggestions").EnumerateArray());
        Assert.NotEmpty(result.GetProperty("Contributors").EnumerateArray());
    }

    // ── inspections ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InspectCode_AppliesTheVbHouseRule()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Two.vb", """
            Public Class First
            End Class

            Public Class Second
            End Class
            """);

        var report = await new InspectCodeService(workspace).RunAsync(
            projectName: null, filePath: null, packs: "house", minSeverity: "info",
            ids: null, excludeIds: null, enableAll: true, maxResults: 100, timeoutSeconds: 60);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("VMC0001", finding.InspectionId);
        Assert.EndsWith("Two.vb", finding.File, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectCode_SaysWhenAPackHasNoVbAnalyzer()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Value() As Integer
                    Return 1
                End Function
            End Class
            """);

        var report = await new InspectCodeService(workspace).RunAsync(
            projectName: null, filePath: null, packs: "rider", minSeverity: "info",
            ids: null, excludeIds: null, enableAll: true, maxResults: 100, timeoutSeconds: 60);

        Assert.Contains(report.Warnings, warning =>
            warning.Contains("rider", StringComparison.OrdinalIgnoreCase) &&
            warning.Contains("no Visual Basic analyzer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNetAnalyzersPack_CoversBothLanguages()
    {
        // The pack is selected only when its assemblies are on this machine; when they are, the VB
        // list must be filled from the package's vb folder rather than reported as C#-only.
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
            End Class
            """);

        var report = await new InspectCodeService(workspace).RunAsync(
            projectName: null, filePath: null, packs: "netanalyzers", minSeverity: "warning",
            ids: null, excludeIds: null, enableAll: false, maxResults: 100, timeoutSeconds: 120);

        if (report.Packs.Count == 0) return;   // the package is not installed on this machine

        Assert.DoesNotContain(report.Warnings, warning =>
            warning.Contains("netanalyzers", StringComparison.OrdinalIgnoreCase) &&
            warning.Contains("no Visual Basic analyzer", StringComparison.Ordinal));
    }
}
