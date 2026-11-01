using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The declaration-side operations of "Transform parameters" on a Visual Basic method: make-optional,
/// make-required and change-type. The two call-site rewrites stay C#-only and are refused with the reason.
/// </summary>
public sealed class VbTransformParametersTests
{
    [Fact]
    public async Task MakeOptional_AddsTheOptionalKeywordAndTheDefault()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Greeter.vb", """
            Public Class Greeter
                Public Function Greet(name As String) As String
                    Return "Hello " & name
                End Function
            End Class
            """);

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "make-optional", ["name"], defaultValue: "\"you\"", dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("Optional name As String = \"you\"", workspace.Read("Greeter.vb"));
    }

    [Fact]
    public async Task MakeRequired_RemovesTheOptionalKeywordAndTheDefault()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Greeter.vb", """
            Public Class Greeter
                Public Function Greet(Optional name As String = "you") As String
                    Return "Hello " & name
                End Function

                Public Function Run() As String
                    Return Greet("x")
                End Function
            End Class
            """);

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "make-required", ["name"], dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Greeter.vb");
        Assert.Contains("Public Function Greet(name As String) As String", text);
        Assert.DoesNotContain("Optional", text);
    }

    [Fact]
    public async Task ChangeType_ReplacesTheAsClause()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Greeter.vb", """
            Public Class Greeter
                Public Function Greet(name As String) As String
                    Return "Hello " & name
                End Function
            End Class
            """);

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "change-type", ["name"], newType: "Object", dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("Public Function Greet(name As Object) As String", workspace.Read("Greeter.vb"));
    }

    [Fact]
    public async Task Wrap_TurnsTheParametersIntoOneObject()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Add(first As Integer, last As Integer) As Integer
                    Return first + last
                End Function

                Public Function Run() As Integer
                    Return Add(1, 2)
                End Function
            End Class
            """);

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Add", "wrap", ["first", "last"], typeName: "AddArgs", dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Public Function Add(args As AddArgs) As Integer", text);
        Assert.Contains("Return args.first + args.last", text);
        Assert.Contains("Public NotInheritable Class AddArgs", text);
        Assert.Contains("Public Sub New(first As Integer, last As Integer)", text);
        Assert.Contains("Return Add(New AddArgs(1, 2))", text);
    }

    [Fact]
    public async Task ReplaceWithProperty_MovesTheParameterToAPropertyAndAssignsItBeforeTheCall()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Greeter.vb", """
            Public Class Greeter
                Private _greeted As String = ""

                Public Function Greet(name As String) As String
                    _greeted = name
                    Return "Hello " & name
                End Function

                Public Function Run() As String
                    Return Greet("x")
                End Function
            End Class
            """);

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "replace-with-property", ["name"], dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Greeter.vb");
        Assert.Contains("Public Property name As String", text);
        Assert.Contains("Public Function Greet() As String", text);
        Assert.Contains("_greeted = Me.name", text);
        Assert.Contains("Me.name = \"x\"", text);
        Assert.Contains("Return Greet()", text);
    }
}
