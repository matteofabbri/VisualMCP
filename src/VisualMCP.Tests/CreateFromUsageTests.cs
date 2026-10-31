using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The create-from-usage shapes added after CS1061/CS0246/CS0103: a missing static member on a type
/// (CS0117) and an overload whose signature is the call site (CS1501/CS7036). Each is compiler-gated.
/// </summary>
public sealed class CreateFromUsageTests
{
    private static async Task<FakeWorkspace> CreateAsync(string source, int line)
    {
        var workspace = FakeWorkspace.Create(("C.cs", source));
        var service = new CreateFromUsageService(workspace);
        var result = await service.CreateFromUsage(workspace.PathOf("C.cs"), line, null, dryRun: false);
        ResultAssert.Succeeded(result);
        return workspace;
    }

    [Fact]
    public async Task Cs0117_AddsTheMissingStaticMemberToTheType()
    {
        const string source = """
            internal static class Api
            {
                public static void Use() => Api.Missing(1);
            }
            """;

        using var workspace = await CreateAsync(source, 3);

        var text = workspace.Read("C.cs");
        Assert.Contains("public static void Missing(int arg1)", text);
        Assert.Contains("System.NotImplementedException", text);
    }

    [Fact]
    public async Task Cs1501_AddsAnOverloadFromTheCallSite()
    {
        const string source = """
            internal class C
            {
                public int Add(int a) => a;

                public int Use() => Add(1, 2);
            }
            """;

        using var workspace = await CreateAsync(source, 5);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Add(int arg1, int arg2)", text);
        Assert.Contains("public int Add(int a)", text);
    }

    [Fact]
    public async Task Cs7036_AddsTheOverloadWithTheMissingArguments()
    {
        const string source = """
            internal class C
            {
                public int Add(int a, int b) => a + b;

                public int Use() => Add(1);
            }
            """;

        using var workspace = await CreateAsync(source, 5);

        Assert.Contains("Add(int arg1)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task AnUnsupportedDiagnosticIsNamedWithTheSupportedSet()
    {
        const string source = """
            internal class C
            {
                public void M()
                {
                    int x = "not an int";
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new CreateFromUsageService(workspace);

        var result = await service.CreateFromUsage(workspace.PathOf("C.cs"), 5, null, dryRun: true);

        Assert.Contains("not one this tool can satisfy", ResultAssert.Error(result));
    }
}
