using Snitch.Languages.CSharp.Inspections;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// <c>apply_code_fix</c> over a VB document: the analyzers and the fixer are chosen for the project's
/// language, so the .NET analyzers' Visual Basic fixers (Basic…Fixer) run instead of their C# twins.
/// </summary>
public sealed class VbCodeFixTests
{
    [Fact]
    public async Task ApplyCodeFix_UsesTheVbNetAnalyzerFixerForAVbDocument()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Friend Class C
                Public Sub Run()
                    System.Console.WriteLine("x")
                End Sub
            End Class
            """);

        var service = new CodeFixService(workspace);
        var result = await service.ApplyAsync(new CodeFixRequest(
            "CA1822", workspace.PathOf("C.vb"), Line: 2, DryRun: true));

        var json = ResultAssert.Json(result);
        if (json.TryGetProperty("error", out var error) &&
            error.GetString()!.Contains("No loaded inspection pack", StringComparison.Ordinal))
            return;   // the NetAnalyzers package is not installed on this machine

        ResultAssert.Succeeded(result);
        Assert.Equal("Visual Basic", json.GetProperty("language").GetString());
        Assert.Contains(json.GetProperty("changedFiles").EnumerateArray(),
            file => file.GetProperty("preview").GetString()!.Contains("Shared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyCodeFix_SaysWhichLanguageItCouldNotFix()
    {
        using var workspace = VbStyleTests.VisualBasicWorkspace.Create("C.vb", """
            Friend Class C
            End Class
            """);

        var service = new CodeFixService(workspace);
        var result = await service.ApplyAsync(new CodeFixRequest(
            "RCS1060", workspace.PathOf("C.vb"), Line: 1, DryRun: true));

        var error = ResultAssert.Error(result);
        Assert.Contains("for Visual Basic", error);
    }
}
