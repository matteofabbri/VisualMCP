using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// change_signature on a Visual Basic document: the same operation as the C# path — reorder, remove and
/// add parameters on the declaration and at every plain call site — with VB's own spelling of an
/// argument and its optional parentheses.
/// </summary>
public sealed class VbChangeSignatureTests
{
    [Fact]
    public async Task ChangeSignature_ReordersTheParametersOfAVisualBasicMethod()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Compute(a As Integer, b As String) As Integer
                    Return a
                End Function

                Public Function Run() As Integer
                    Return Compute(1, "x")
                End Function
            End Class
            """);

        var service = new ChangeSignatureService(workspace);
        var result = await service.ChangeSignature("Compute", "Calculator", new[] { "b", "a" });
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Compute(b As String, a As Integer)", text);
        Assert.Contains("Compute(\"x\", 1)", text);
    }

    [Fact]
    public async Task ChangeSignature_RewritesACallWithoutParentheses()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Sub Show(message As String, count As Integer)
                End Sub

                Public Sub Run()
                    Show "hello", 2
                End Sub
            End Class
            """);

        var service = new ChangeSignatureService(workspace);
        var result = await service.ChangeSignature("Show", "Calculator", new[] { "count", "message" });
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Show(count As Integer, message As String)", text);
        Assert.Contains("Show 2, \"hello\"", text);
    }

    [Fact]
    public async Task ChangeSignature_AddsAParameterAndPassesNothingAtEveryCall()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Sub Trace(message As String)
                End Sub

                Public Sub Run()
                    Trace("start")
                End Sub
            End Class
            """);

        var service = new ChangeSignatureService(workspace);
        var result = await service.ChangeSignature(
            "Trace",
            "Calculator",
            new[] { "message" },
            additionalParams: [new ChangeSignatureService.NewParameter { Name = "level", Type = "Integer", DefaultValue = "0" }]);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Trace(message As String, Optional level As Integer = 0)", text);
        Assert.Contains("Trace(\"start\", 0)", text);
    }
}
