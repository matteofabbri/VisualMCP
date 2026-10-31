using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The second batch of context actions: the reverse of concatenation→interpolation, digit separators,
/// removing an argument name, the dictionary-key and enum-defined guards, and the loop inversion.
/// </summary>
public sealed class ContextActionLiteralAndGuardTests
{
    private static async Task<(FakeWorkspace Workspace, ContextActionService Service)> ApplyAsync(
        string source, string action, int line, string? text = null)
    {
        var workspace = FakeWorkspace.Create(("C.cs", source));
        var service = new ContextActionService(workspace);
        var result = await service.Apply(action, workspace.PathOf("C.cs"), line, text: text, dryRun: false);
        ResultAssert.Succeeded(result);
        return (workspace, service);
    }

    [Fact]
    public async Task InterpolatedToConcat_TurnsHolesIntoOperands()
    {
        var (workspace, _) = await ApplyAsync("""
            namespace Probe;

            internal class C
            {
                public string M(int x) => $"a{x}b";
            }
            """, "interpolated_to_concat", 5);

        using var scope = workspace;
        Assert.Contains("=> \"a\" + x + \"b\";", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task InterpolatedToConcat_RefusesAFormattedHole()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string M(double x) => $"a{x:F2}";
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("interpolated_to_concat", workspace.PathOf("C.cs"), 5);

        Assert.Contains("alignment or a format specifier", ResultAssert.Error(result));
    }

    [Fact]
    public async Task DigitSeparators_GroupsFromTheRight()
    {
        var (workspace, _) = await ApplyAsync("""
            namespace Probe;

            internal class C
            {
                public long M() => 1000000L;
            }
            """, "digit_separators", 5);

        using var scope = workspace;
        Assert.Contains("=> 1_000_000L;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task DigitSeparators_RefusesAHexadecimalLiteral()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M() => 0xFFAA00;
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("digit_separators", workspace.PathOf("C.cs"), 5);

        Assert.Contains("hexadecimal or binary", ResultAssert.Error(result));
    }

    [Fact]
    public async Task RemoveArgumentName_MakesTheArgumentPositional()
    {
        var (workspace, _) = await ApplyAsync("""
            namespace Probe;

            internal class C
            {
                public int M() => Add(a: 1, b: 2);

                private static int Add(int a, int b) => a + b;
            }
            """, "remove_argument_name", 5, text: "a");

        using var scope = workspace;
        Assert.Contains("Add(1, b: 2)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task CheckDictionaryKey_InsertsAKeyGuard()
    {
        var (workspace, _) = await ApplyAsync("""
            using System.Collections.Generic;

            namespace Probe;

            internal class C
            {
                public int M()
                {
                    var map = new Dictionary<string, int>();
                    return map["a"];
                }
            }
            """, "check_dictionary_key", 10, text: "\"a\"");

        using var scope = workspace;
        var text = workspace.Read("C.cs");
        Assert.Contains("if (!map.ContainsKey(\"a\"))", text);
        Assert.Contains("throw new KeyNotFoundException", text);
        Assert.Contains("using System.Collections.Generic;", text);
    }

    [Fact]
    public async Task CheckDictionaryKey_RefusesANonDictionaryIndexer()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M(int[] items) => items[0];
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("check_dictionary_key", workspace.PathOf("C.cs"), 5);

        Assert.Contains("ContainsKey", ResultAssert.Error(result));
    }

    [Fact]
    public async Task CheckEnumDefined_GuardsTheEnumParameter()
    {
        var (workspace, _) = await ApplyAsync("""
            namespace Probe;

            internal enum Color { Red, Blue }

            internal class C
            {
                public void M(Color color)
                {
                }
            }
            """, "check_enum_defined", 7);

        using var scope = workspace;
        var text = workspace.Read("C.cs");
        Assert.Contains("if (!Enum.IsDefined(typeof(Color), color))", text);
        Assert.Contains("throw new ArgumentOutOfRangeException(nameof(color), color, \"Unknown Color value.\");", text);
        Assert.Contains("using System;", text);
    }

    [Fact]
    public async Task InvertLoop_MakesTheCanonicalLoopDescending()
    {
        var (workspace, _) = await ApplyAsync("""
            namespace Probe;

            internal class C
            {
                public void M(int[] items)
                {
                    for (var i = 0; i < items.Length; i++)
                    {
                        System.Console.WriteLine(items[i]);
                    }
                }
            }
            """, "invert_loop", 7);

        using var scope = workspace;
        Assert.Contains("for (var i = items.Length - 1; i >= 0; i--)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task InvertLoop_RefusesALoopThatDoesNotStartAtZero()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void M()
                {
                    for (var i = 1; i < 10; i++)
                    {
                    }
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("invert_loop", workspace.PathOf("C.cs"), 7);

        Assert.Contains("does not start at zero", ResultAssert.Error(result));
    }

    [Fact]
    public void TheNewActionsAreInTheCatalogue()
    {
        foreach (var id in new[]
                 {
                     "interpolated_to_concat", "digit_separators", "remove_argument_name",
                     "check_dictionary_key", "check_enum_defined", "invert_loop",
                 })
            Assert.Contains(ContextActionService.Catalogue, entry => entry.Id == id);
    }
}
