using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The statement-shape refactorings on a Visual Basic document: a local, a field, a parameter, a part of
/// a string, a negated Boolean and a copied type. Every path is compiler-gated, so a shape the operation
/// cannot prove is refused with the reason.
/// </summary>
public sealed class VbStatementRefactoringsTests
{
    [Fact]
    public async Task IntroduceVariable_DeclaresTheLocalAndReplacesEveryOccurrence()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calc.vb", """
            Public Class Calc
                Public Function Run() As Integer
                    Return 41 + 41
                End Function
            End Class
            """);

        var result = await new IntroduceVariableService(workspace)
            .IntroduceVariable(workspace.PathOf("Calc.vb"), 3, "41", "offset");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Calc.vb");
        Assert.Contains("Dim offset As Integer = 41", text);
        Assert.Contains("Return offset + offset", text);
    }

    [Fact]
    public async Task IntroduceField_HoistsAContextFreeExpressionIntoAReadOnlyField()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calc.vb", """
            Public Class Calc
                Public Function Run() As Integer
                    Return 41
                End Function
            End Class
            """);

        var result = await new IntroduceFieldService(workspace)
            .IntroduceField(workspace.PathOf("Calc.vb"), 3, "41", "offset");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Calc.vb");
        Assert.Contains("Private ReadOnly offset As Integer = 41", text);
        Assert.Contains("Return offset", text);
    }

    [Fact]
    public async Task IntroduceField_RefusesAnExpressionThatReadsTheInstance()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calc.vb", """
            Public Class Calc
                Private _value As Integer = 41

                Public Function Run() As Integer
                    Return _value
                End Function
            End Class
            """);

        var result = await new IntroduceFieldService(workspace)
            .IntroduceField(workspace.PathOf("Calc.vb"), 5, "_value", "copy");

        Assert.Contains("instance member", ResultAssert.Error(result));
    }

    [Fact]
    public async Task IntroduceParameter_AddsTheParameterAndPassesItAtEveryCallSite()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Add(value As Integer) As Integer
                    Return value + 41
                End Function

                Public Function Run() As Integer
                    Return Add(1)
                End Function
            End Class
            """);

        var result = await new IntroduceParameterService(workspace)
            .IntroduceParameter(workspace.PathOf("Calculator.vb"), 3, "41", "offset");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Public Function Add(value As Integer, offset As Integer) As Integer", text);
        Assert.Contains("Return value + offset", text);
        Assert.Contains("Return Add(1, 41)", text);
    }

    [Fact]
    public async Task IntroduceParameter_RefusesAnExpressionThatDependsOnTheCaller()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Double(value As Integer) As Integer
                    Return value + value
                End Function
            End Class
            """);

        var result = await new IntroduceParameterService(workspace)
            .IntroduceParameter(workspace.PathOf("Calculator.vb"), 3, "value + value", "copy");

        Assert.Contains("not fixed at compile time", ResultAssert.Error(result));
    }

    [Fact]
    public async Task IntroducePartOfString_LiftsTheSubstringIntoAConstant()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Messages.vb", """
            Public Class Messages
                Public Function Greet() As String
                    Return "Prefix: Value"
                End Function
            End Class
            """);

        var result = await new IntroducePartOfStringService(workspace)
            .IntroducePartOfString(workspace.PathOf("Messages.vb"), 3, "Value", "Extracted");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Messages.vb");
        Assert.Contains("Private Const Extracted As String = \"Value\"", text);
        Assert.Contains("Return \"Prefix: \" & Extracted", text);
    }

    [Fact]
    public async Task InvertBoolean_FlipsEveryReturnAndRenamesTheMember()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Switch.vb", """
            Public Class Switch
                Public Function IsReady() As Boolean
                    Return True
                End Function

                Public Function Run() As Boolean
                    Return IsReady()
                End Function
            End Class
            """);

        var result = await new InvertBooleanService(workspace)
            .Invert(workspace.PathOf("Switch.vb"), "IsReady", newName: "IsBlocked");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Switch.vb");
        Assert.Contains("Function IsBlocked() As Boolean", text);
        Assert.Contains("Return False", text);
        Assert.Contains("Return IsBlocked()", text);
    }

    [Fact]
    public async Task CopyType_DuplicatesTheDeclarationAndKeepsTheConstructorSpelling()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Order.vb", """
            Public Class Order
                Public Property Id As Integer

                Public Sub New(id As Integer)
                    Me.Id = id
                End Sub
            End Class
            """);

        var result = await new CopyTypeService(workspace)
            .CopyType(workspace.PathOf("Order.vb"), "Order", "OrderCopy");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Order.vb");
        Assert.Contains("Public Class OrderCopy", text);
        Assert.Contains("Public Class Order", text);
        Assert.Contains("Public Sub New(id As Integer)", text);
    }
}
