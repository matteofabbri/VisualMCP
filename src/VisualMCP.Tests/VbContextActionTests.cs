using Snitch.Languages.VB.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Visual Basic context-action engine over in-memory VB projects: one case per implemented id, the
/// refusals that keep the family honest, and the catalogue rules that tie the two tables together.
/// Every case runs with dryRun: false and asserts on the file that was written.
/// </summary>
public sealed class VbContextActionTests
{
    // ── conversions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportType_AddsTheImportsOfAMetadataType()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(s As StringBuilder) As Integer
                    Return s.Length
                End Function
            End Class
            """, "import_type", line: 2, text: "StringBuilder");

        Assert.Contains("Imports System.Text", text);
    }

    [Fact]
    public async Task ImportType_RefusesWhenTheTypeIsAlreadyInScope()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Imports System.Text

            Friend Class C
                Public Function F(s As StringBuilder) As Integer
                    Return s.Length
                End Function
            End Class
            """);

        var service = new VbContextActionService(workspace);
        var result = await service.Apply("import_type", workspace.PathOf("C.vb"), 4, text: "StringBuilder");

        Assert.Contains("already in scope", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ConcatToInterpolated_TurnsTheChainIntoAnInterpolatedString()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(s As String) As String
                    Return "a" & s & "b"
                End Function
            End Class
            """, "concat_to_interpolated", line: 3);

        Assert.Contains("Return $\"a{s}b\"", text);
    }

    [Fact]
    public async Task InterpolatedToConcat_TurnsTheHolesIntoAnAmpersandChain()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(s As String) As String
                    Return $"a{s}b"
                End Function
            End Class
            """, "interpolated_to_concat", line: 3);

        Assert.Contains("Return \"a\" & s & \"b\"", text);
    }

    [Fact]
    public async Task LinqQueryToMethod_WritesTheMethodChain()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(xs As Integer()) As Integer()
                    Return From x In xs Where x > 0 Order By x Descending Select x * 2
                End Function
            End Class
            """, "linq_query_to_method", line: 3);

        Assert.Contains("Return xs.Where(Function(x) x > 0).OrderByDescending(Function(x) x).Select(Function(x) x * 2)", text);
    }

    [Fact]
    public async Task LinqMethodToQuery_WritesTheQueryExpression()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(xs As Integer()) As Integer()
                    Return xs.Where(Function(x) x > 0).Select(Function(x) x * 2)
                End Function
            End Class
            """, "linq_method_to_query", line: 3);

        Assert.Contains("Return From x In xs Where x > 0 Select x * 2", text);
    }

    [Fact]
    public async Task DigitSeparators_GroupsAnInteger()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F() As Integer
                    Dim n As Integer = 1000000
                    Return n
                End Function
            End Class
            """, "digit_separators", line: 3);

        Assert.Contains("Dim n As Integer = 1_000_000", text);
    }

    [Fact]
    public async Task RemoveDigitSeparators_RemovesTheUnderscores()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F() As Integer
                    Dim n As Integer = 1_000_000
                    Return n
                End Function
            End Class
            """, "remove_digit_separators", line: 3);

        Assert.Contains("Dim n As Integer = 1000000", text);
    }

    [Fact]
    public async Task RemoveArgumentName_MakesTheArgumentPositional()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(value As String)
                    Run(value:="x")
                End Sub
            End Class
            """, "remove_argument_name", line: 3, text: "value");

        Assert.Contains("Run(\"x\")", text);
    }

    [Fact]
    public async Task UseVar_RemovesTheExplicitType()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F() As Integer
                    Dim n As Integer = 1
                    Return n
                End Function
            End Class
            """, "use_var", line: 3);

        Assert.Contains("Dim n = 1", text);
    }

    [Fact]
    public async Task UseExplicitType_WritesTheInferredType()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F() As Integer
                    Dim n = 1
                    Return n
                End Function
            End Class
            """, "use_explicit_type", line: 3);

        Assert.Contains("Dim n As Integer = 1", text);
    }

    [Fact]
    public async Task UseConfigureAwaitFalse_AppendsTheSuffix()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Async Function F(task As System.Threading.Tasks.Task) As System.Threading.Tasks.Task
                    Await task
                End Function
            End Class
            """, "use_configure_await_false", line: 3);

        Assert.Contains("Await task.ConfigureAwait(False)", text);
    }

    [Fact]
    public async Task SplitDeclaration_WritesOneDimPerDeclarator()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    Dim a As Integer = 1, b As Integer = 2
                End Sub
            End Class
            """, "split_declaration", line: 3);

        Assert.Contains("Dim a As Integer = 1", text);
        Assert.Contains("Dim b As Integer = 2", text);
    }

    [Fact]
    public async Task ConcatToStringBuilder_RewritesTheDeclarationAndTheAppends()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    Dim s As String = ""
                    s &= "a"
                    s &= "b"
                End Sub
            End Class
            """, "concat_to_stringbuilder", line: 3);

        Assert.Contains("Dim s As New System.Text.StringBuilder()", text);
        Assert.Contains("s.Append(\"a\")", text);
        Assert.Contains("s.Append(\"b\")", text);
    }

    [Fact]
    public async Task ConcatToStringBuilder_RefusesWhenTheLocalIsReadElsewhere()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Friend Class C
                Public Function Run() As String
                    Dim s As String = ""
                    s &= "a"
                    Return s
                End Function
            End Class
            """);

        var service = new VbContextActionService(workspace);
        var result = await service.Apply("concat_to_stringbuilder", workspace.PathOf("C.vb"), 3);

        Assert.Contains("is read on line", ResultAssert.Error(result));
    }

    // ── guards ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckParamNull_AddsTheGuardAtTheTopOfTheBody()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(value As String)
                    System.Console.WriteLine(value)
                End Sub
            End Class
            """, "check_param_null", line: 2, text: "value");

        Assert.Contains("If value Is Nothing Then Throw New ArgumentNullException(NameOf(value))", text);
    }

    [Fact]
    public async Task CheckAllParamsNull_GuardsEveryReferenceTypeParameter()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(first As String, second As Object)
                    System.Console.WriteLine(first)
                    System.Console.WriteLine(second)
                End Sub
            End Class
            """, "check_all_params_null", line: 2);

        Assert.Contains("ArgumentNullException(NameOf(first))", text);
        Assert.Contains("ArgumentNullException(NameOf(second))", text);
    }

    [Fact]
    public async Task CheckStringNotNullOrEmpty_UsesIsNullOrEmpty()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(value As String)
                    System.Console.WriteLine(value)
                End Sub
            End Class
            """, "check_string_not_null_or_empty", line: 2, text: "value");

        Assert.Contains("If String.IsNullOrEmpty(value) Then", text);
    }

    [Fact]
    public async Task CheckCollectionNotEmpty_UsesCount()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(items As System.Collections.Generic.List(Of Integer))
                    System.Console.WriteLine(items.Count)
                End Sub
            End Class
            """, "check_collection_not_empty", line: 2, text: "items");

        Assert.Contains("If items Is Nothing OrElse items.Count = 0 Then", text);
    }

    [Fact]
    public async Task CheckVariableNull_InsertsTheGuardAfterTheDeclaration()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    Dim value As String = "x"
                    System.Console.WriteLine(value)
                End Sub
            End Class
            """, "check_variable_null", line: 3, text: "value");

        Assert.Contains("If value Is Nothing Then Throw New InvalidOperationException(", text);
        Assert.True(text.IndexOf("Dim value As String", StringComparison.Ordinal) <
                    text.IndexOf("If value Is Nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckDictionaryKey_GuardsTheIndexer()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(d As System.Collections.Generic.Dictionary(Of String, Integer), key As String) As Integer
                    Return d(key)
                End Function
            End Class
            """, "check_dictionary_key", line: 3, text: "key");

        Assert.Contains("If Not d.ContainsKey(key) Then Throw New KeyNotFoundException(", text);
    }

    [Fact]
    public async Task CheckEnumDefined_GuardsTheEnumParameter()
    {
        var text = await ApplyAsync("""
            Friend Enum Color
                Red
            End Enum

            Friend Class C
                Public Sub Run(color As Color)
                    System.Console.WriteLine(color)
                End Sub
            End Class
            """, "check_enum_defined", line: 6, text: "color");

        Assert.Contains("If Not [Enum].IsDefined(GetType(Color), color) Then", text);
        Assert.Contains("NameOf(color), color, \"Unknown Color value.\"", text);
    }

    [Fact]
    public async Task CheckIndexRange_GuardsTheIndexAgainstCount()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Function F(items As System.Collections.Generic.List(Of Integer), index As Integer) As Integer
                    Return items(index)
                End Function
            End Class
            """, "check_index_range", line: 3, text: "index");

        Assert.Contains("If index < 0 OrElse index >= items.Count Then", text);
    }

    // ── wrapping ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WrapInTryCatch_WrapsTheWholeRangeAndKeepsTheBody()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    System.Console.WriteLine(1)
                    System.Console.WriteLine(2)
                End Sub
            End Class
            """, "wrap_in_try_catch", line: 3, toLine: 4);

        Assert.Contains("Try", text);
        Assert.Contains("Catch ex As Exception", text);
        Assert.Contains("Throw", text);
        Assert.Contains("End Try", text);
        Assert.Contains("System.Console.WriteLine(1)", text);
        Assert.Contains("System.Console.WriteLine(2)", text);
    }

    [Fact]
    public async Task WrapInTryFinally_WrapsTheRange()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    System.Console.WriteLine(1)
                    System.Console.WriteLine(2)
                End Sub
            End Class
            """, "wrap_in_try_finally", line: 3, toLine: 4);

        Assert.Contains("Finally", text);
        Assert.Contains("End Try", text);
    }

    [Fact]
    public async Task WrapInIf_UsesTheConditionFromText()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    System.Console.WriteLine(1)
                End Sub
            End Class
            """, "wrap_in_if", line: 3, text: "System.DateTime.Now.Year > 2000");

        Assert.Contains("If System.DateTime.Now.Year > 2000 Then", text);
        Assert.Contains("End If", text);
    }

    [Fact]
    public async Task WrapInUsing_WrapsTheRangeInTheResource()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(stream As System.IO.MemoryStream)
                    System.Console.WriteLine(1)
                End Sub
            End Class
            """, "wrap_in_using", line: 3, text: "stream");

        Assert.Contains("Using stream", text);
        Assert.Contains("End Using", text);
    }

    [Fact]
    public async Task WrapInRegion_WrapsTheWholeLines()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run()
                    System.Console.WriteLine(1)
                    System.Console.WriteLine(2)
                End Sub
            End Class
            """, "wrap_in_region", line: 3, toLine: 4, text: "Demo");

        Assert.Contains("#Region \"Demo\"", text);
        Assert.Contains("#End Region", text);
    }

    [Fact]
    public async Task Wrap_RefusesARangeThatCutsAStatementInHalf()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Friend Class C
                Public Sub Run()
                    System.Console.WriteLine(
                        1)
                End Sub
            End Class
            """);

        var service = new VbContextActionService(workspace);
        var result = await service.Apply("wrap_in_try_catch", workspace.PathOf("C.vb"), 4, toLine: 4);

        Assert.Contains("cuts one in half", ResultAssert.Error(result));
    }

    // ── block shapes ────────────────────────────────────────────────────────

    [Fact]
    public async Task MergeSequentialChecks_CombinesTheTwoConditions()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(a As Boolean, b As Boolean)
                    If a Then
                        If b Then
                            System.Console.WriteLine("x")
                        End If
                    End If
                End Sub
            End Class
            """, "merge_sequential_checks", line: 3);

        Assert.Contains("If (a) AndAlso (b) Then", text);
    }

    [Fact]
    public async Task SplitAndInIf_NestsTheTwoConditions()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(a As Boolean, b As Boolean)
                    If a AndAlso b Then
                        System.Console.WriteLine("x")
                    End If
                End Sub
            End Class
            """, "split_and_in_if", line: 3);

        Assert.Contains("If a Then", text);
        Assert.Contains("If b Then", text);
    }

    [Fact]
    public async Task JoinNullCheckWithUsage_ReplacesTheIfWithConditionalAccess()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(s As String)
                    If s IsNot Nothing Then
                        s.Trim()
                    End If
                End Sub
            End Class
            """, "join_null_check_with_usage", line: 3);

        Assert.Contains("s?.Trim()", text);
    }

    [Fact]
    public async Task IfToSwitch_WritesASelectCaseWithTheElseArm()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(value As Integer)
                    If value = 1 Then
                        System.Console.WriteLine("one")
                    ElseIf value = 2 Then
                        System.Console.WriteLine("two")
                    Else
                        System.Console.WriteLine("other")
                    End If
                End Sub
            End Class
            """, "if_to_switch", line: 3);

        Assert.Contains("Select Case value", text);
        Assert.Contains("Case 1", text);
        Assert.Contains("Case 2", text);
        Assert.Contains("Case Else", text);
        Assert.Contains("End Select", text);
    }

    // ── loops ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ForeachToFor_WritesTheIndexedLoop()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(items As System.Collections.Generic.List(Of Integer))
                    For Each item As Integer In items
                        System.Console.WriteLine(item)
                    Next
                End Sub
            End Class
            """, "foreach_to_for", line: 3);

        Assert.Contains("For i = 0 To items.Count - 1", text);
        Assert.Contains("Dim item As Integer = items(i)", text);
    }

    [Fact]
    public async Task ForToForeach_WritesTheForEachLoopAndDropsTheItemLocal()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(items As System.Collections.Generic.List(Of Integer))
                    For i = 0 To items.Count - 1
                        Dim item As Integer = items(i)
                        System.Console.WriteLine(item)
                    Next
                End Sub
            End Class
            """, "for_to_foreach", line: 3);

        Assert.Contains("For Each item As Integer In items", text);
        Assert.DoesNotContain("items(i)", text);
    }

    [Fact]
    public async Task InvertLoop_CountsDown()
    {
        var text = await ApplyAsync("""
            Friend Class C
                Public Sub Run(n As Integer)
                    For i = 0 To n
                        System.Console.WriteLine(i)
                    Next
                End Sub
            End Class
            """, "invert_loop", line: 3);

        Assert.Contains("For i = n - 1 To 0 Step -1", text);
    }

    // ── catalogue and refusals ──────────────────────────────────────────────

    [Fact]
    public async Task NotApplicable_IsRefusedWithTheReason()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Friend Class C
            End Class
            """);

        var service = new VbContextActionService(workspace);
        var result = await service.Apply("string_to_raw", workspace.PathOf("C.vb"), 1);

        var error = ResultAssert.Error(result);
        Assert.Contains("does not apply to Visual Basic", error);
        Assert.Contains("raw string literal", error);
    }

    [Fact]
    public async Task UnknownAction_ReturnsTheCatalogueAndTheRefusals()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Friend Class C
            End Class
            """);

        var service = new VbContextActionService(workspace);
        var json = ResultAssert.Json(await service.Apply("make_coffee", workspace.PathOf("C.vb"), 1));

        Assert.Contains("Unknown context action", json.GetProperty("error").GetString());
        Assert.Contains(json.GetProperty("catalogue").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "check_param_null");
        Assert.Contains(json.GetProperty("notApplicable").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "add_braces");
    }

    [Fact]
    public void VbCatalogue_EveryIdHasAnImplementationAndEveryRefusalAReason()
    {
        var ids = VbContextActionService.Catalogue.Select(entry => entry.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var refused = VbContextActionService.NotApplicable.Select(entry => entry.Id).ToList();
        Assert.Empty(ids.Intersect(refused, StringComparer.OrdinalIgnoreCase));

        Assert.All(VbContextActionService.NotApplicable, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            Assert.False(entry.Reason.Contains("TODO", StringComparison.OrdinalIgnoreCase));
        });

        // Every id the C# engine advertises is either implemented here or refused with a reason: the two
        // tables together cover the shared catalogue, so nothing reads as "not found".
        var csharp = Snitch.Languages.CSharp.ContextActions.ContextActionService.Catalogue
            .Select(entry => entry.Id);
        Assert.Empty(csharp.Except(ids, StringComparer.OrdinalIgnoreCase)
            .Except(refused, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<string> ApplyAsync(
        string source, string action, int line, int? toLine = null, string? text = null)
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", source);
        var service = new VbContextActionService(workspace);
        var result = await service.Apply(action, workspace.PathOf("C.vb"), line, toLine, text, dryRun: false);
        ResultAssert.Succeeded(result);
        return workspace.Read("C.vb");
    }
}
