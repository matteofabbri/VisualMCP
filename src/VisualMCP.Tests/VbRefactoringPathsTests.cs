using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The refactorings that used to stop at a C# syntax tree, now on a Visual Basic document: the same
/// operation written in VB's own vocabulary — <c>Interface</c> plus <c>Implements</c> clauses, an
/// auto-<c>Property</c>, a <c>ReadOnly Property</c> with a <c>Get</c> block, a private <c>Function</c>
/// extracted out of a <c>Sub</c>/<c>Function</c> body — and every one of them compiler-gated.
/// </summary>
public sealed class VbRefactoringPathsTests
{
    [Fact]
    public async Task ExtractInterface_WritesAVisualBasicInterfaceAndPatchesEveryMemberThatImplementsIt()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Private _count As Integer

                Public Property Name As String

                Public Function Add(a As Integer) As Integer
                    _count += a
                    Return _count
                End Function
            End Class
            """);

        var result = await new ExtractInterfaceService(workspace)
            .ExtractInterface("Calculator", "ICalculator", dryRun: false);
        ResultAssert.Succeeded(result);

        var interfaceText = workspace.Read("ICalculator.vb");
        Assert.Contains("Public Interface ICalculator", interfaceText);
        Assert.Contains("Property Name As String", interfaceText);
        Assert.Contains("Function Add(a As Integer) As Integer", interfaceText);
        Assert.EndsWith("End Interface" + Environment.NewLine, interfaceText);

        var classText = workspace.Read("Calculator.vb");
        Assert.Contains("Public Class Calculator", classText);
        Assert.Contains("    Implements ICalculator", classText);
        Assert.Contains("Implements ICalculator.Name", classText);
        Assert.Contains("Implements ICalculator.Add", classText);
    }

    [Fact]
    public async Task MoveType_LiftsAVisualBasicTypeIntoAFileOfItsOwn()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Types.vb", """
            Imports System

            Namespace ProbeVb.Model

                Public Class First
                End Class

                Public Class Second
                    Public Function Name() As String
                        Return "second"
                    End Function
                End Class

            End Namespace
            """);

        var result = await new MoveTypeService(workspace).MoveType("Second", targetDirectory: null, dryRun: false);
        ResultAssert.Succeeded(result);

        var moved = workspace.Read("Second.vb");
        Assert.Contains("Namespace ProbeVb.Model", moved);
        Assert.Contains("Public Class Second", moved);
        Assert.EndsWith("End Namespace" + Environment.NewLine, moved);

        var remaining = workspace.Read("Types.vb");
        Assert.Contains("Public Class First", remaining);
        Assert.DoesNotContain("Public Class Second", remaining);
    }

    [Fact]
    public async Task PropertyToAuto_FoldsAVisualBasicGetSetPairAndDropsTheBackingField()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Private _name As String = "unnamed"

                Public Property Name As String
                    Get
                        Return _name
                    End Get
                    Set(value As String)
                        _name = value
                    End Set
                End Property
            End Class
            """);

        var result = await new PropertyToAutoService(workspace)
            .PropertyToAuto(workspace.PathOf("Person.vb"), "Name");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Person.vb");
        Assert.Contains("Public Property Name As String = \"unnamed\"", text);
        Assert.DoesNotContain("_name", text);
        Assert.DoesNotContain("End Get", text);
    }

    [Fact]
    public async Task PropertyToAuto_RefusesWhenTheFieldIsUsedElsewhere()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Private _name As String

                Public Property Name As String
                    Get
                        Return _name
                    End Get
                    Set(value As String)
                        _name = value
                    End Set
                End Property

                Public Function HasName() As Boolean
                    Return _name IsNot Nothing
                End Function
            End Class
            """);

        var result = await new PropertyToAutoService(workspace)
            .PropertyToAuto(workspace.PathOf("Person.vb"), "Name");

        Assert.Contains("used", ResultAssert.Error(result));
    }

    [Fact]
    public async Task FunctionToProperty_TurnsAVisualBasicFunctionIntoAReadOnlyPropertyAndRewritesBothCallSpellings()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Counter.vb", """
            Public Class Counter
                Private _count As Integer

                Public Function GetCount() As Integer
                    Return _count
                End Function

                Public Function Run() As Integer
                    Return GetCount() + GetCount
                End Function
            End Class
            """);

        var result = await new FunctionToPropertyService(workspace)
            .FunctionToProperty(workspace.PathOf("Counter.vb"), "GetCount");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Counter.vb");
        Assert.Contains("Public ReadOnly Property Count As Integer", text);
        Assert.Contains("End Get", text);
        Assert.Contains("Return Count + Count", text);
        Assert.DoesNotContain("GetCount", text);
    }

    [Fact]
    public async Task ExtractMethod_MovesAVisualBasicBlockIntoAPrivateFunction()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Calculator.vb", """
            Public Class Calculator
                Public Function Run(a As Integer) As Integer
                    Dim doubled As Integer = a * 2
                    Return doubled + 1
                End Function
            End Class
            """);

        var result = await new ExtractMethodService(workspace)
            .ExtractMethod(workspace.PathOf("Calculator.vb"), 3, 4, "Compute");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Calculator.vb");
        Assert.Contains("Return Compute(a)", text);
        Assert.Contains("Private Function Compute(a As Integer) As Integer", text);
        Assert.Contains("Dim doubled As Integer = a * 2", text);
        Assert.Equal(2, text.Split("End Function").Length - 1);
        Assert.Contains("End Class", text);
    }

    [Fact]
    public async Task MoveToFolder_DerivesTheFolderFromAVisualBasicNamespaceBlock()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Widget.vb", """
            Namespace ProbeVb.Model

                Public Class Widget
                End Class

            End Namespace
            """);

        var result = await new MoveToFolderService(workspace).MoveToFolder(workspace.PathOf("Widget.vb"));
        ResultAssert.Succeeded(result);

        Assert.True(File.Exists(Path.Combine(workspace.PathOf("Model"), "Widget.vb")));
        Assert.False(File.Exists(workspace.PathOf("Widget.vb")));
    }

    [Fact]
    public async Task InlineField_SubstitutesTheInitializerAndRemovesTheField()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Config.vb", """
            Public Class Config
                Private _retries As Integer = 3

                Public Function Run() As Integer
                    Return _retries + _retries
                End Function
            End Class
            """);

        var result = await new InlineFieldService(workspace)
            .InlineField(workspace.PathOf("Config.vb"), "_retries");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Config.vb");
        Assert.DoesNotContain("_retries", text);
        Assert.Contains("Return (3) + (3)", text);
    }

    [Fact]
    public async Task InlineField_RefusesAFieldThatIsWritten()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Config.vb", """
            Public Class Config
                Private _retries As Integer = 3

                Public Sub Run()
                    _retries = 4
                End Sub
            End Class
            """);

        var result = await new InlineFieldService(workspace)
            .InlineField(workspace.PathOf("Config.vb"), "_retries");

        Assert.Contains("written", ResultAssert.Error(result));
    }

    [Fact]
    public async Task MoveToNamespace_RenamesTheBlockAndAddsTheImportTheReferencesNeed()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Model.vb", """
            Namespace ProbeVb.Old
                Public Class Widget
                End Class
            End Namespace
            """);

        var project = workspace.CurrentSolution!.Projects.Single();
        var consumerPath = workspace.PathOf("Consumer.vb");
        const string consumer = """
            Imports ProbeVb.Old

            Namespace ProbeVb.App
                Public Class Consumer
                    Public Function Make() As Widget
                        Return New Widget()
                    End Function
                End Class
            End Namespace
            """;
        File.WriteAllText(consumerPath, consumer);
        workspace.TryAddDocument(project.Id, "Consumer.vb", consumerPath, Microsoft.CodeAnalysis.Text.SourceText.From(consumer));

        var result = await new MoveToNamespaceService(workspace)
            .MoveToNamespace(workspace.PathOf("Model.vb"), "ProbeVb.New");
        ResultAssert.Succeeded(result);

        Assert.Contains("Namespace ProbeVb.New", workspace.Read("Model.vb"));

        var rewritten = workspace.Read("Consumer.vb");
        Assert.Contains("Imports ProbeVb.New", rewritten);
        Assert.DoesNotContain("Imports ProbeVb.Old", rewritten);
    }

    [Fact]
    public async Task PropertyToFunction_TurnsAVisualBasicPropertyIntoReadMethods()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Public Property Name As String

                Public Function Greet() As String
                    Return "Hello " & Name
                End Function
            End Class
            """);

        var result = await new PropertyToFunctionService(workspace)
            .PropertyToFunction(workspace.PathOf("Person.vb"), "Name");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Person.vb");
        Assert.Contains("Public Function GetName() As String", text);
        Assert.Contains("Public Sub SetName(value As String)", text);
        Assert.Contains("Return \"Hello \" & GetName()", text);
        Assert.DoesNotContain("Property Name", text);
    }

    [Fact]
    public async Task PropertyToFunction_RefusesAPropertyThatIsAssigned()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Public Property Name As String

                Public Sub Rename()
                    Name = "other"
                End Sub
            End Class
            """);

        var result = await new PropertyToFunctionService(workspace)
            .PropertyToFunction(workspace.PathOf("Person.vb"), "Name");

        Assert.Contains("assigned", ResultAssert.Error(result));
    }
}
