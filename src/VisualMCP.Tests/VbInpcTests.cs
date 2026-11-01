using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// "Implement INotifyPropertyChanged" on a Visual Basic class: the Implements clause, the event, the
/// caller-name helper and the backing-field properties that raise it.
/// </summary>
public sealed class VbInpcTests
{
    [Fact]
    public async Task ImplementInpc_AddsTheContractAndConvertsTheAutoProperty()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Public Property Name As String
            End Class
            """);

        var result = await new ImplementInpcService(workspace).ImplementInpc("Person");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Person.vb");
        Assert.Contains("Implements System.ComponentModel.INotifyPropertyChanged", text);
        Assert.Contains("Public Event PropertyChanged As System.ComponentModel.PropertyChangedEventHandler", text);
        Assert.Contains("Protected Sub OnPropertyChanged", text);
        Assert.Contains("Private _name As String", text);
        Assert.Contains("Public Property Name As String", text);
        Assert.Contains("OnPropertyChanged()", text);
    }

    [Fact]
    public async Task ImplementInpc_ConvertsOnlyTheRequestedProperties()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Class Person
                Public Property Name As String
                Public Property Age As Integer
            End Class
            """);

        var result = await new ImplementInpcService(workspace).ImplementInpc("Person", ["Name"]);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Person.vb");
        Assert.Contains("Private _name As String", text);
        Assert.DoesNotContain("Private _age As Integer", text);
        Assert.Contains("Public Property Age As Integer", text);
    }
}
