using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// "Generify" on a Visual Basic method or type: the concrete type becomes <c>(Of T)</c>, and a type
/// parameter that cannot be inferred from the parameters is supplied as an explicit <c>(Of Concrete)</c>
/// at every call site or reference.
/// </summary>
public sealed class VbGenerifyTests
{
    [Fact]
    public async Task GenerifyMethod_AbstractsAParameterAndInfersItAtTheCall()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Identity(value As Integer) As Integer
                    Return value
                End Function

                Public Function Run() As Integer
                    Return Identity(3)
                End Function
            End Class
            """);

        var result = await new GenerifyService(workspace).Generify(
            workspace.PathOf("Calculator.vb"), methodName: "Identity", usages: ["Integer"], dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Public Function Identity(Of T)(value As T) As T", text);
        Assert.Contains("Return Identity(3)", text);
    }

    [Fact]
    public async Task GenerifyMethod_SuppliesTheTypeArgumentWhenInferenceIsImpossible()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Factory.vb", """
            Public Class Factory
                Public Function Make() As List(Of Integer)
                    Return New List(Of Integer)()
                End Function

                Public Function Run() As List(Of Integer)
                    Return Make()
                End Function
            End Class
            """);

        var result = await new GenerifyService(workspace).Generify(
            workspace.PathOf("Factory.vb"), methodName: "Make", usages: ["Integer"], dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Factory.vb");
        Assert.Contains("Public Function Make(Of T)() As List(Of T)", text);
        Assert.Contains("Return Make(Of Integer)()", text);
    }

    [Fact]
    public async Task GenerifyType_AddsTheParameterAndSpellsTheArgumentAtEveryReference()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Box.vb", """
            Public Class Box
                Public Value As Integer
            End Class

            Public Class Runner
                Public Function Run() As Integer
                    Dim box As New Box()
                    Return box.Value
                End Function
            End Class
            """);

        var result = await new GenerifyService(workspace).Generify(
            workspace.PathOf("Box.vb"), typeName: "Box", usages: ["Integer"], dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Box.vb");
        Assert.Contains("Public Class Box(Of T)", text);
        Assert.Contains("Public Value As T", text);
        Assert.Contains("New Box(Of Integer)()", text);
    }

    [Fact]
    public async Task Generify_RefusesAnAlreadyGenericDeclaration()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Cache.vb", """
            Public Class Cache(Of T)
                Public Value As T
            End Class
            """);

        var result = await new GenerifyService(workspace).Generify(
            workspace.PathOf("Cache.vb"), typeName: "Cache", usages: ["Integer"], dryRun: true);

        Assert.Contains("already has type parameter", ResultAssert.Error(result));
    }
}
