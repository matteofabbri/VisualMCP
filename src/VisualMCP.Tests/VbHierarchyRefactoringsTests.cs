using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Pull members up into an interface (VB's signature plus the Implements clause every member needs) or
/// into a base class, and push one member down into the only derived type that uses it.
/// </summary>
public sealed class VbHierarchyRefactoringsTests
{
    [Fact]
    public async Task PullMembersUp_IntoAnInterfaceWritesTheSignatureAndTheImplementsClause()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Person.vb", """
            Public Interface INamed
            End Interface

            Public Class Person
                Implements INamed

                Public Function GetName() As String
                    Return "x"
                End Function
            End Class
            """);

        var result = await new PullMembersUpService(workspace)
            .PullMembersUp("Person", ["GetName"], "INamed");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Person.vb");
        Assert.Contains("Function GetName() As String", text);
        Assert.Contains("Public Function GetName() As String Implements INamed.GetName", text);
    }

    [Fact]
    public async Task PullMembersUp_IntoABaseClassMovesTheWholeDeclaration()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Types.vb", """
            Public Class Base
            End Class

            Public Class Derived
                Inherits Base

                Public Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function
            End Class
            """);

        var result = await new PullMembersUpService(workspace)
            .PullMembersUp("Derived", ["Twice"], "Base");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Types.vb");
        var baseIndex = text.IndexOf("Public Class Base", StringComparison.Ordinal);
        var derivedIndex = text.IndexOf("Public Class Derived", StringComparison.Ordinal);
        var methodIndex = text.IndexOf("Public Function Twice", StringComparison.Ordinal);

        Assert.True(methodIndex > baseIndex && methodIndex < derivedIndex, text);
        Assert.Contains("Return value * 2", text);
    }

    [Fact]
    public async Task PushMembersDown_MovesTheMemberIntoTheOnlyDerivedType()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Animals.vb", """
            Public Class Animal
                Public Function Speak() As String
                    Return "?"
                End Function
            End Class

            Public Class Dog
                Inherits Animal
            End Class
            """);

        var result = await new PushMembersDownService(workspace)
            .PushMembersDown(workspace.PathOf("Animals.vb"), "Speak", "Animal");

        ResultAssert.Succeeded(result);
        var text = workspace.Read("Animals.vb");
        var dogIndex = text.IndexOf("Public Class Dog", StringComparison.Ordinal);
        Assert.True(text.IndexOf("Public Function Speak", StringComparison.Ordinal) > dogIndex, text);
        Assert.DoesNotContain("Public Function Speak", text[..dogIndex]);
        Assert.Contains("Return \"?\"", text);
    }

    [Fact]
    public async Task PushMembersDown_RefusesAUseOutsideTheDerivedType()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Animals.vb", """
            Public Class Animal
                Public Function Speak() As String
                    Return "?"
                End Function
            End Class

            Public Class Dog
                Inherits Animal
            End Class

            Public Class Runner
                Public Function Run() As String
                    Return New Dog().Speak()
                End Function
            End Class
            """);

        var result = await new PushMembersDownService(workspace)
            .PushMembersDown(workspace.PathOf("Animals.vb"), "Speak", "Animal");

        Assert.Contains("outside the single derived type", ResultAssert.Error(result));
    }
}
