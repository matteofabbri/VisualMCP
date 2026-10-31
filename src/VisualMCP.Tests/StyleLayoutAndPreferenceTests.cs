using Snitch.Languages.CSharp.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The style rules that only act when the repository configured a preference: the var direction
/// (IDE0007/IDE0008), parentheses for clarity (IDE0048) and the blank-line layout family (IDE2000–IDE2006).
/// Each test states the option, because without it every rule must do nothing.
/// </summary>
public sealed class StyleLayoutAndPreferenceTests
{
    private static FakeWorkspace Workspace(string editorConfig, string source) =>
        FakeWorkspace.CreateWithEditorConfig(editorConfig, ("C.cs", source));

    [Fact]
    public async Task IDE0008_turns_varIntoTheExplicitType_when_theCategoryAsksForIt()
    {
        using var workspace = Workspace("""
            [*.cs]
            csharp_style_var_when_type_is_apparent = false
            """, """
            namespace Probe;

            internal class Foo { }

            internal class C
            {
                public Foo M() => null!;

                public void Run()
                {
                    var value = new Foo();
                }
            }
            """);

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0008", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("Foo value = new Foo();", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task IDE0007_turnsTheExplicitTypeIntoVar_inTheSameCategory()
    {
        using var workspace = Workspace("""
            [*.cs]
            csharp_style_var_when_type_is_apparent = true
            """, """
            namespace Probe;

            internal class Foo { }

            internal class C
            {
                public void Run()
                {
                    Foo value = new Foo();
                }
            }
            """);

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0007", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("var value = new Foo();", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task The_varCategoryOption_doesNotApplyToAnotherCategory()
    {
        using var workspace = Workspace("""
            [*.cs]
            csharp_style_var_when_type_is_apparent = true
            """, """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    int count = 1;
                }
            }
            """);

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0007", dryRun: false);
        ResultAssert.Succeeded(result);

        // The built-in category is unset, so the apparent-type preference has nothing to say here.
        Assert.Contains("int count = 1;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task IDE0048_addsParentheses_aroundADifferentOperator()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_parentheses_in_arithmetic_binary_operators = always_for_clarity
            """, """
            namespace Probe;

            internal class C
            {
                public int M(int a, int b, int c) => a + b * c;
            }
            """);

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0048", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("a + (b * c)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task IDE2000_collapsesTwoBlankLinesToOne()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_multiple_blank_lines_experimental = false
            """, "namespace Probe;\n\ninternal class C\n{\n    public int A() => 1;\n\n\n    public int B() => 2;\n}\n");

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2000", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int A() => 1;\n\n    public int B()", text.Replace("\r\n", "\n"));
        Assert.DoesNotContain("\n\n\n", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task IDE2000_doesNothing_withoutTheOption()
    {
        using var workspace = FakeWorkspace.Create(("C.cs",
            "namespace Probe;\n\ninternal class C\n{\n    public int A() => 1;\n\n\n    public int B() => 2;\n}\n"));

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2000", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("\n\n\n", workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task IDE2001_movesAnEmbeddedStatementToItsOwnLine()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_embedded_statements_on_same_line_experimental = false
            """, """
            namespace Probe;

            internal class C
            {
                public void M(bool flag)
                {
                    if (flag) System.Console.WriteLine(1);
                }
            }
            """);

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2001", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.DoesNotContain("if (flag) System.Console", text);
        Assert.Matches(@"if \(flag\)\r?\n\s+System\.Console\.WriteLine\(1\);", text);
    }

    [Fact]
    public async Task IDE2002_removesTheBlankLine_inAnEmptyBlock()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_blank_lines_between_consecutive_braces_experimental = false
            """, "namespace Probe;\n\ninternal class C\n{\n    public void M(bool flag)\n    {\n        if (flag)\n        {\n\n        }\n    }\n}\n");

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2002", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.DoesNotContain("{\n\n        }", workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task IDE2003_addsTheBlankLine_afterABlock()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_statement_immediately_after_block_experimental = false
            """, """
            namespace Probe;

            internal class C
            {
                public void M(bool flag)
                {
                    if (flag)
                    {
                        System.Console.WriteLine(1);
                    }
                    System.Console.WriteLine(2);
                }
            }
            """);

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2003", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Matches(@"\}\r?\n\s*\r?\n\s+System\.Console\.WriteLine\(2\);", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task IDE2004_removesTheBlankLine_afterTheConstructorColon()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_blank_line_after_colon_in_constructor_initializer_experimental = false
            """, "namespace Probe;\n\ninternal class B { protected B() { } }\n\ninternal class C : B\n{\n    public C() :\n\n        base()\n    {\n    }\n}\n");

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2004", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.DoesNotContain(":\n\n", workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task IDE2005_removesTheBlankLine_inAConditionalExpression()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_blank_line_after_token_in_conditional_expression_experimental = false
            """, "namespace Probe;\n\ninternal class C\n{\n    public int M(bool flag)\n    {\n        return flag ?\n\n            1 : 2;\n    }\n}\n");

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2005", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.DoesNotContain("?\n\n", workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task IDE2006_removesTheBlankLine_afterTheArrow()
    {
        using var workspace = Workspace("""
            [*.cs]
            dotnet_style_allow_blank_line_after_token_in_arrow_expression_clause_experimental = false
            """, "namespace Probe;\n\ninternal class C\n{\n    public int M() =>\n\n        1;\n}\n");

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE2006", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.DoesNotContain("=>\n\n", workspace.Read("C.cs").Replace("\r\n", "\n"));
    }

    [Fact]
    public void EveryIdInTheTable_HasExactlyOneCategory()
    {
        var rewritten = SimplifyCodeService.Rules.Select(rule => rule.Id).ToList();
        var explained = StyleRewriteCatalog.Delegated
            .Concat(StyleRewriteCatalog.NotApplicable)
            .Concat(StyleRewriteCatalog.NotRewrites)
            .Select(entry => entry.Id)
            .ToList();

        foreach (var id in new[] { "IDE0007", "IDE0008", "IDE0048", "IDE2000", "IDE2001", "IDE2002", "IDE2003", "IDE2004", "IDE2005", "IDE2006" })
            Assert.Contains(id, rewritten);

        foreach (var id in new[] { "IDE0005", "IDE0032", "IDE0290", "IDE1006", "IDE3000" })
            Assert.Contains(id, explained);

        var all = rewritten.Concat(explained).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, SimplifyCodeService.NotRewritten.Count + rewritten.Count);

        // Microsoft's table has 126 ids: 97 rewritten, 29 delegated / not applicable / not a rewrite.
        Assert.Equal(126, all.Count);
        Assert.Equal(97, rewritten.Count);
        Assert.Equal(29, explained.Count);
    }
}
