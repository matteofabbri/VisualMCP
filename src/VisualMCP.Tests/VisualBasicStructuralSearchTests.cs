using System.Text.Json;
using Snitch.Languages.CSharp.Search;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Structural search and replace over Visual Basic: a VB-shaped pattern with <c>$holes$</c> is parsed
/// inside a wrapper (VB has no free-standing "parse a statement"), matched against the VB syntax tree,
/// and a replacement is spliced back with the match's own trivia. The matching core is the C# one —
/// kind, tokens and holes — so the two languages share the engine, not just the tool.
/// </summary>
public sealed class VisualBasicStructuralSearchTests
{
    [Fact]
    public async Task Search_FindsVbInvocationsAndBindsTheHoles()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function

                Public Function Run() As Integer
                    Dim first = Twice(2)
                    Return Twice(first)
                End Function
            End Class
            """);

        var result = await Service(workspace).Execute(
            "Twice($argument$)", replacement: null, "document", workspace.PathOf("C.vb"), null, dryRun: true, maxResults: 50);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("matchCount").GetInt32());

        var languages = json.GetProperty("languages").EnumerateArray().Select(entry => entry.GetString()).ToList();
        Assert.Equal(["vb"], languages);

        var bindings = json.GetProperty("matches").EnumerateArray()
            .Select(match => match.GetProperty("bindings").GetProperty("$argument$").GetString())
            .ToList();
        Assert.Equal(["2", "first"], bindings);
    }

    [Fact]
    public async Task Search_MatchesAVbNullTest()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Valid(customer As Object) As Boolean
                    If customer Is Nothing Then
                        Return False
                    End If
                    Return True
                End Function
            End Class
            """);

        var result = await Service(workspace).Execute(
            "$x$ Is Nothing", replacement: null, "document", workspace.PathOf("C.vb"), null, dryRun: true, maxResults: 50);

        var match = Assert.Single(ResultAssert.Json(result).GetProperty("matches").EnumerateArray());
        Assert.Equal("customer", match.GetProperty("bindings").GetProperty("$x$").GetString());
        Assert.Equal(3, match.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Replace_RewritesTheMatchInPlace()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function
            End Class
            """);

        var result = await Service(workspace).Execute(
            "Return $value$", "Return ($value$)", "document", workspace.PathOf("C.vb"), null,
            dryRun: false, maxResults: 50);

        ResultAssert.Succeeded(result);
        Assert.Equal(1, ResultAssert.Json(result).GetProperty("matchCount").GetInt32());
        Assert.Contains("Return (value * 2)", workspace.Read("C.vb"));
    }

    [Fact]
    public async Task Search_ParsesThePatternPerLanguageInAMixedSolution()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.CreateWithCSharpFile(
            ("C.vb", """
                Public Class VbProbe
                    Public Function Twice(value As Integer) As Integer
                        Return value * 2
                    End Function

                    Public Function Run() As Integer
                        Return Twice(2)
                    End Function
                End Class
                """),
            ("C.cs", """
                internal class CsProbe
                {
                    private int Twice(int value) => value * 2;

                    public int Run() => Twice(2);
                }
                """));

        var result = await Service(workspace).Execute(
            "Twice($argument$)", replacement: null, "solution", null, null, dryRun: true, maxResults: 50);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("matchCount").GetInt32());

        var matches = json.GetProperty("matches").EnumerateArray().ToList();
        Assert.Contains(matches, match => match.GetProperty("language").GetString() == "vb"
                                          && (match.GetProperty("file").GetString() ?? "").EndsWith("C.vb", StringComparison.Ordinal));
        Assert.Contains(matches, match => match.GetProperty("language").GetString() == "cs"
                                          && (match.GetProperty("file").GetString() ?? "").EndsWith("C.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_LanguageSelectionNarrowsTheScope()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.CreateWithCSharpFile(
            ("C.vb", """
                Public Class VbProbe
                    Public Function Twice(value As Integer) As Integer
                        Return value * 2
                    End Function

                    Public Function Run() As Integer
                        Return Twice(2)
                    End Function
                End Class
                """),
            ("C.cs", """
                internal class CsProbe
                {
                    private int Twice(int value) => value * 2;

                    public int Run() => Twice(2);
                }
                """));

        var result = await Service(workspace).Execute(
            "Twice($argument$)", replacement: null, "solution", null, null, dryRun: true, maxResults: 50, language: "vb");

        var json = ResultAssert.Json(result);
        var match = Assert.Single(json.GetProperty("matches").EnumerateArray());
        Assert.Equal("vb", match.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Search_ACSharpPatternLeavesVbDocumentsAloneAndSaysSo()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.CreateWithCSharpFile(
            ("C.vb", """
                Public Class VbProbe
                    Public Function Value() As Integer
                        Return 1
                    End Function
                End Class
                """),
            ("C.cs", """
                internal class CsProbe
                {
                    public string? Value() => null;

                    public string? IsIt() => Value() as string;
                }
                """));

        var result = await Service(workspace).Execute(
            "$x$ as string", replacement: null, "solution", null, null, dryRun: true, maxResults: 50);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("matchCount").GetInt32());
        Assert.Equal(["cs"], json.GetProperty("languages").EnumerateArray().Select(entry => entry.GetString()));

        var warnings = json.GetProperty("warnings").EnumerateArray()
            .Select(warning => warning.GetString() ?? "")
            .ToList();
        Assert.Contains(warnings, warning => warning.StartsWith("Visual Basic:", StringComparison.Ordinal));
    }

    private static StructuralSearchService Service(VbStyleTests.VisualBasicWorkspace workspace) => new(workspace);
}
