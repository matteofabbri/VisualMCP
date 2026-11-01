using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// "Move to global usings" on a Visual Basic project: the per-file <c>Imports</c> become project-level
/// <c>&lt;Import Include="…" /&gt;</c> items, the files lose the statement, and the gate compiles the
/// edited project with the new global imports applied to its options.
/// </summary>
public sealed class VbGlobalUsingsTests
{
    [Fact]
    public async Task MoveToGlobalUsings_WritesTheProjectItemsAndRemovesTheStatements()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("Program.vb", """
            Imports System.Text
            Imports System.IO

            Public Class Program
                Public Function Run() As String
                    Return New StringBuilder().Append("x").ToString()
                End Function
            End Class
            """);

        await File.WriteAllTextAsync(workspace.PathOf("ProbeVb.vbproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>ProbeVb</RootNamespace>
              </PropertyGroup>
            </Project>
            """);

        var result = await new GlobalUsingsService(workspace).MoveToGlobalUsings(projectName: "ProbeVb", dryRun: false);

        ResultAssert.Succeeded(result);

        var source = workspace.Read("Program.vb");
        Assert.DoesNotContain("Imports System.Text", source);
        Assert.DoesNotContain("Imports System.IO", source);

        var project = workspace.Read("ProbeVb.vbproj");
        Assert.Contains("<Import Include=\"System.Text\" />", project);
        Assert.Contains("<Import Include=\"System.IO\" />", project);
    }
}
