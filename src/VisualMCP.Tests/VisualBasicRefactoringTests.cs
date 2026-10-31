using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The refactorings that are not C#-idiom-specific, applied to Visual Basic: rename through Roslyn's
/// renamer, encapsulate field (a field plus a property with <c>Get</c>/<c>Set</c>), and the
/// <c>ParamArray</c> modifier in both directions. Each write goes through the same compiler gate the
/// C# services use, so the tests read the files back from disk.
/// </summary>
public sealed class VisualBasicRefactoringTests
{
    [Fact]
    public async Task Rename_RewritesAVbSymbolAndItsReferences()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function

                Public Function Run() As Integer
                    Return Twice(2)
                End Function
            End Class
            """);

        var result = await new ApplyRenameService(workspace).ApplyRename("Twice", "Doubled", containingType: null);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("C.vb");
        Assert.Contains("Function Doubled(value As Integer)", text);
        Assert.Contains("Return Doubled(2)", text);
        Assert.DoesNotContain("Twice", text);
    }

    [Fact]
    public async Task EncapsulateField_AddsAVbPropertyAndRewritesTheReferences()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Private _value As Integer

                Public Function Read() As Integer
                    Return _value
                End Function

                Public Sub Write(value As Integer)
                    _value = value
                End Sub
            End Class
            """);

        var result = await new EncapsulateFieldService(workspace).EncapsulateField("_value", containingType: null);

        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.vb");
        Assert.Contains("Public Property Value As Integer", text);
        Assert.Contains("Get", text);
        Assert.Contains("Return _value", text);
        Assert.Contains("Set(value As Integer)", text);
        Assert.Contains("Return Value", text);
        Assert.Contains("Value = value", text);
    }

    [Fact]
    public async Task AddAndRemoveParams_SpellTheVbModifier()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function Join(values() As String) As String
                    Return String.Concat(values)
                End Function
            End Class
            """);

        var add = await new AddParamsModifierService(workspace).AddParams(workspace.PathOf("C.vb"), "Join");
        ResultAssert.Succeeded(add);
        Assert.Contains("ParamArray values() As String", workspace.Read("C.vb"));

        var remove = await new RemoveParamsModifierService(workspace).RemoveParams(workspace.PathOf("C.vb"), "Join");
        ResultAssert.Succeeded(remove);
        Assert.Contains("Function Join(values() As String)", workspace.Read("C.vb"));
        Assert.DoesNotContain("ParamArray", workspace.Read("C.vb"));
    }

    [Fact]
    public async Task AddParams_RefusesAnInterfaceImplementation()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Interface IJoin
                Function Join(values() As String) As String
            End Interface

            Public Class C
                Implements IJoin

                Public Function Join(values() As String) As String Implements IJoin.Join
                    Return String.Concat(values)
                End Function
            End Class
            """);

        var result = await new AddParamsModifierService(workspace).AddParams(workspace.PathOf("C.vb"), "Join");

        // Two declarations carry the name: the interface's and the implementation's.
        Assert.Contains("overload", ResultAssert.Error(result));
    }

    [Fact]
    public async Task ChangeType_ReplacesTheTypeOfAVbDeclaration()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Public Function F() As Integer
                    Dim count As Integer = 0
                    Return count
                End Function
            End Class
            """);

        var result = await new ChangeTypeService(workspace).ChangeType(workspace.PathOf("C.vb"), 3, "Long");

        ResultAssert.Succeeded(result);
        Assert.Contains("Dim count As Long", workspace.Read("C.vb"));
    }

    [Fact]
    public async Task UseBaseType_ReplacesTheTypeWithAnInterface()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Imports System.Collections.Generic

            Public Class C
                Private _items As List(Of Integer) = New List(Of Integer)

                Public Function Count() As Integer
                    Return _items.Count
                End Function
            End Class
            """);

        var result = await new UseBaseTypeService(workspace).UseBaseType(workspace.PathOf("C.vb"), 4, "IEnumerable");

        ResultAssert.Succeeded(result);
        Assert.Contains("As IEnumerable(Of Integer)", workspace.Read("C.vb"));
    }

    [Fact]
    public async Task TypeToPartial_MarksEveryVbDeclarationPartial()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
            End Class
            """);

        var result = await new TypeToPartialService(workspace).TypeToPartial(workspace.PathOf("C.vb"), "C");

        ResultAssert.Succeeded(result);
        Assert.Contains("Public Partial Class C", workspace.Read("C.vb"));
    }

    [Fact]
    public async Task SafeDelete_RemovesAnUnreferencedVbMember()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Public Class C
                Private Sub Unused()
                End Sub

                Public Sub Run()
                End Sub
            End Class
            """);

        var result = await new SafeDeleteService(workspace).SafeDelete("Unused", containingType: null);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("C.vb");
        Assert.DoesNotContain("Unused", text);
        Assert.Contains("Public Sub Run", text);
    }
}
