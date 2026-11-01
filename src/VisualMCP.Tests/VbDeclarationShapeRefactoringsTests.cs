using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The declaration-shape refactorings that used to stop at a C# syntax tree and now run on a Visual
/// Basic document too: the inline family beyond a field, the move family beyond a type, the two
/// extractions that create a type, and the indexer conversions in VB's own <c>Default Property</c>
/// vocabulary. Every one is compiler-gated, so a shape the operation cannot prove is refused.
/// </summary>
public sealed class VbDeclarationShapeRefactoringsTests
{
    [Fact]
    public async Task InlineSymbol_SubstitutesAVisualBasicLocalAndDropsItsDeclaration()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Config.vb", """
            Public Class Config
                Public Function Run() As Integer
                    Dim retries As Integer = 3
                    Return retries + retries
                End Function
            End Class
            """);

        var result = await new InlineSymbolService(workspace).InlineSymbol("retries");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Config.vb");
        Assert.DoesNotContain("Dim retries", text);
        Assert.Contains("Return (3) + (3)", text);
    }

    [Fact]
    public async Task InlineSymbol_SubstitutesTheArgumentsOfASingleReturnFunction()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Counter.vb", """
            Public Class Counter
                Public Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function

                Public Function Run() As Integer
                    Return Twice(3)
                End Function
            End Class
            """);

        var result = await new InlineSymbolService(workspace).InlineSymbol("Twice");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Counter.vb");
        Assert.DoesNotContain("Function Twice", text);
        Assert.Contains("Return (3 * 2)", text);
    }

    [Fact]
    public async Task InlineParameter_RemovesTheParameterAndTheArgumentAtEveryCallSite()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Add(value As Integer) As Integer
                    Return value + 1
                End Function

                Public Function Run() As Integer
                    Return Add(4) + Add(4)
                End Function
            End Class
            """);

        var result = await new InlineParameterService(workspace)
            .InlineParameter(workspace.PathOf("Calculator.vb"), "Add", "value");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Public Function Add() As Integer", text);
        Assert.Contains("Return (4) + 1", text);
        Assert.Contains("Return Add() + Add()", text);
    }

    [Fact]
    public async Task InlineParameter_RefusesCallSitesThatPassDifferentValues()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Add(value As Integer) As Integer
                    Return value + 1
                End Function

                Public Function Run() As Integer
                    Return Add(4) + Add(5)
                End Function
            End Class
            """);

        var result = await new InlineParameterService(workspace)
            .InlineParameter(workspace.PathOf("Calculator.vb"), "Add", "value");

        Assert.Contains("different values", ResultAssert.Error(result));
    }

    [Fact]
    public async Task InlineClass_PushesTheHelperMembersIntoTheOwnerAndDropsBoth()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Owner.vb", """
            Public Class Owner
                Private ReadOnly _helper As New Helper()

                Public Function Run() As Integer
                    Return _helper.Twice(2)
                End Function
            End Class

            Friend Class Helper
                Public Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function
            End Class
            """);

        var result = await new InlineClassService(workspace)
            .InlineClass(workspace.PathOf("Owner.vb"), "Owner", "_helper");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Owner.vb");
        Assert.DoesNotContain("Class Helper", text);
        Assert.DoesNotContain("_helper", text);
        Assert.Contains("Function Twice(value As Integer) As Integer", text);
        Assert.Contains("Return Twice(2)", text);
    }

    [Fact]
    public async Task MoveStaticMembers_RepointsTheQualifiedCallAndTheDeclaration()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Helpers.vb", """
            Public Class MathHelper
                Public Shared Function Double(value As Integer) As Integer
                    Return value * 2
                End Function
            End Class

            Public Class Consumer
                Public Function Run() As Integer
                    Return MathHelper.Double(4)
                End Function
            End Class
            """);

        var result = await new MoveStaticMembersService(workspace).MoveStaticMembers(
            workspace.PathOf("Helpers.vb"), "MathHelper", ["Double"], "Consumer");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Helpers.vb");
        Assert.Contains("Consumer.Double(4)", text);
        Assert.DoesNotContain("MathHelper.Double(4)", text);
        Assert.Contains("Public Class Consumer", text);
        Assert.Contains("Public Shared Function Double", text);
    }

    [Fact]
    public async Task MoveInstanceMethod_TurnsTheMethodIntoASharedMethodWithItsReceiver()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Order.vb", """
            Public Class Order
                Public _total As Integer

                Public Function TotalWithTax() As Integer
                    Return _total * 2
                End Function
            End Class

            Public Class Presenter
            End Class
            """);

        var result = await new MoveInstanceMethodService(workspace).MoveInstanceMethod(
            workspace.PathOf("Order.vb"), "Order", "TotalWithTax", "Presenter", "source");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Order.vb");
        Assert.Contains("Public Shared Function TotalWithTax(source As Order) As Integer", text);
        Assert.Contains("Return source._total * 2", text);

        var order = text[..text.IndexOf("End Class", StringComparison.Ordinal)];
        Assert.DoesNotContain("TotalWithTax", order);
    }

    [Fact]
    public async Task MoveTypeToOuterScope_LiftsTheNestedTypeBesideItsContainer()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Outer.vb", """
            Public Class Outer
                Public Class Inner
                    Public Function Value() As Integer
                        Return 1
                    End Function
                End Class

                Public Function Make() As Inner
                    Return New Inner()
                End Function
            End Class
            """);

        var result = await new MoveTypeToOuterScopeService(workspace)
            .MoveTypeToOuterScope(workspace.PathOf("Outer.vb"), "Inner");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Outer.vb");
        Assert.Contains("Public Class Inner", text);

        var innerIndex = text.IndexOf("Public Class Inner", StringComparison.Ordinal);
        var outerEnd = text.IndexOf("End Class", StringComparison.Ordinal);
        Assert.True(innerIndex > outerEnd, "Inner should be a sibling of Outer, after its End Class.");
    }

    [Fact]
    public async Task MoveTypeToNestedScope_MovesTheTypeIntoAnotherType()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Types.vb", """
            Public Class Outer
            End Class

            Public Class Inner
                Public Function Value() As Integer
                    Return 1
                End Function
            End Class
            """);

        var result = await new MoveTypeToNestedScopeService(workspace).MoveTypeToNestedScope(
            workspace.PathOf("Types.vb"), "Inner", "Outer", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Types.vb");
        var outerStart = text.IndexOf("Public Class Outer", StringComparison.Ordinal);
        var innerStart = text.IndexOf("Public Class Inner", StringComparison.Ordinal);
        var outerEnd = text.IndexOf("End Class", StringComparison.Ordinal);

        Assert.True(innerStart > outerStart && innerStart < outerEnd,
            "Inner should now be nested inside Outer.");
    }

    [Fact]
    public async Task ExtractClass_CreatesTheNewClassAndForwardsEveryMember()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Public Property Name As String

                Public Function Greet(greeting As String) As String
                    Return greeting & Name
                End Function
            End Class
            """);

        var result = await new ExtractClassService(workspace).ExtractClass(
            workspace.PathOf("Person.vb"), "Person", ["Name", "Greet"], "PersonData");
        ResultAssert.Succeeded(result);

        var created = workspace.Read("PersonData.vb");
        Assert.Contains("Friend NotInheritable Class PersonData", created);
        Assert.Contains("Public Property Name As String", created);
        Assert.Contains("Function Greet(greeting As String) As String", created);

        var owner = workspace.Read("Person.vb");
        Assert.Contains("Private ReadOnly _personData As New PersonData()", owner);
        Assert.Contains("Return _personData.Greet(greeting)", owner);
        Assert.Contains("Return _personData.Name", owner);
    }

    [Fact]
    public async Task ExtractSuperclass_CreatesTheBaseAndAddsTheInheritsClause()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Order.vb", """
            Public Class Order
                Public Property Id As Integer

                Public Function Describe() As String
                    Return "order " & Id
                End Function
            End Class
            """);

        var result = await new ExtractSuperclassService(workspace).ExtractSuperclass(
            workspace.PathOf("Order.vb"), "Order", ["Id", "Describe"], "OrderBase");
        ResultAssert.Succeeded(result);

        var created = workspace.Read("OrderBase.vb");
        Assert.Contains("Class OrderBase", created);
        Assert.Contains("Public Property Id As Integer", created);
        Assert.Contains("Function Describe() As String", created);

        var derived = workspace.Read("Order.vb");
        Assert.Contains("Inherits OrderBase", derived);
        Assert.DoesNotContain("Function Describe", derived);
    }

    [Fact]
    public async Task ExtractSuperclass_RefusesAMemberThatReadsWhatStaysBehind()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Order.vb", """
            Public Class Order
                Private _secret As Integer

                Public Function Describe() As String
                    Return "order " & _secret
                End Function
            End Class
            """);

        var result = await new ExtractSuperclassService(workspace).ExtractSuperclass(
            workspace.PathOf("Order.vb"), "Order", ["Describe"], "OrderBase");

        Assert.Contains("not being extracted", ResultAssert.Error(result));
    }

    [Fact]
    public async Task FunctionToIndexer_TurnsTheFunctionIntoADefaultProperty()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Shelf.vb", """
            Public Class Shelf
                Private ReadOnly _items As New List(Of String)

                Public Function GetItem(index As Integer) As String
                    Return _items(index)
                End Function

                Public Function First() As String
                    Return GetItem(0)
                End Function
            End Class
            """);

        var result = await new IndexerConversionsService(workspace)
            .FunctionToIndexer(workspace.PathOf("Shelf.vb"), "GetItem");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Shelf.vb");
        Assert.Contains("Default Public ReadOnly Property Item(index As Integer) As String", text);
        Assert.Contains("Return Me(0)", text);
        Assert.DoesNotContain("GetItem", text);
    }

    [Fact]
    public async Task IndexerToFunction_TurnsTheDefaultPropertyIntoGetAndSet()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Shelf.vb", """
            Public Class Shelf
                Private ReadOnly _items As New List(Of String)

                Default Public ReadOnly Property Item(index As Integer) As String
                    Get
                        Return _items(index)
                    End Get
                End Property

                Public Function First() As String
                    Return Me(0)
                End Function
            End Class
            """);

        var result = await new IndexerConversionsService(workspace)
            .IndexerToFunction(workspace.PathOf("Shelf.vb"));
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Shelf.vb");
        Assert.Contains("Public Function [Get](index As Integer) As String", text);
        Assert.Contains("Return Me.[Get](0)", text);
        Assert.DoesNotContain("Default Public ReadOnly Property", text);
    }
}
