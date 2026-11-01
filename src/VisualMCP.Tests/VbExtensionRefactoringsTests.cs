using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The extension-method family and the anonymous-type conversion on a Visual Basic document: VB's
/// <c>&lt;Extension&gt; Function M(receiver As T)</c> in both directions, and
/// <c>New With {.Name = value}</c> into a declared class.
/// </summary>
public sealed class VbExtensionRefactoringsTests
{
    [Fact]
    public async Task StaticToExtension_AddsTheAttributeAndRewritesTheCallSite()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Strings.vb", """
            Imports System.Runtime.CompilerServices

            Public Module Strings
                Public Function Twice(value As String) As String
                    Return value & value
                End Function
            End Module

            Public Class Runner
                Public Function Run() As String
                    Return Strings.Twice("a")
                End Function
            End Class
            """);

        var result = await new StaticToExtensionService(workspace)
            .StaticToExtension(workspace.PathOf("Strings.vb"), "Twice");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Strings.vb");
        Assert.Contains("<Extension>", text);
        Assert.Contains("Function Twice(value As String) As String", text);
        Assert.Contains("Return (\"a\").Twice()", text);
    }

    [Fact]
    public async Task StaticToExtension_RefusesAMethodOutsideAModule()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Numbers.vb", """
            Public Class Numbers
                Public Shared Function Twice(value As Integer) As Integer
                    Return value + value
                End Function
            End Class
            """);

        var result = await new StaticToExtensionService(workspace)
            .StaticToExtension(workspace.PathOf("Numbers.vb"), "Twice");

        Assert.Contains("Module", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ExtensionToStatic_RemovesTheAttributeAndRewritesTheCallSite()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Strings.vb", """
            Imports System.Runtime.CompilerServices

            Public Module Strings
                <Extension>
                Public Function Twice(value As String) As String
                    Return value & value
                End Function
            End Module

            Public Class Runner
                Public Function Run() As String
                    Return "a".Twice()
                End Function
            End Class
            """);

        var result = await new ExtensionToStaticService(workspace)
            .Convert(workspace.PathOf("Strings.vb"), "Twice");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Strings.vb");
        Assert.DoesNotContain("<Extension>", text);
        Assert.Contains("Function Twice(value As String) As String", text);
        Assert.Contains("Return Strings.Twice(\"a\")", text);
    }

    [Fact]
    public async Task ConvertAnonymousToType_DeclaresAClassAndRewritesTheCreation()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Factory.vb", """
            Public Class Factory
                Public Function Make() As Object
                    Dim order = New With {.Id = 1, .Name = "x"}
                    Return order
                End Function
            End Class
            """);

        var result = await new ConvertAnonymousToTypeService(workspace)
            .ConvertAnonymousToType(workspace.PathOf("Factory.vb"), 3, "Order");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Factory.vb");
        Assert.Contains("Friend NotInheritable Class Order", text);
        Assert.Contains("Public ReadOnly Property Id As Integer", text);
        Assert.Contains("Public ReadOnly Property Name As String", text);
        Assert.Contains("Public Sub New(id As Integer, name As String)", text);
        Assert.Contains("Dim order = New Order(1, \"x\")", text);
    }
}
