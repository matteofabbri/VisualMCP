using Snitch.Languages.CSharp.Style;
using Snitch.Languages.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The rules that only act when the repository's <c>.editorconfig</c> asks for something. Each test
/// feeds the option through a real analyzer-config document, so what is exercised is the whole chain:
/// file → Roslyn options provider → StyleOptions → rule.
/// </summary>
public sealed class SimplifyCodeOptionsTests
{
    private const string Braces = """
        root = true

        [*.cs]
        csharp_prefer_braces = true
        """;

    private const string FileScoped = """
        root = true

        [*.cs]
        csharp_style_namespace_declarations = file_scoped
        """;

    private const string ModifierOrder = """
        root = true

        [*.cs]
        csharp_preferred_modifier_order = public, private, protected, internal, file, static, extern, new, virtual, abstract, sealed, override, readonly, unsafe, required, volatile, async
        """;

    private const string ForeachCast = """
        root = true

        [*.cs]
        dotnet_style_prefer_foreach_explicit_cast_in_source = always
        """;

    private const string Accessibility = """
        root = true

        [*.cs]
        dotnet_style_require_accessibility_modifiers = always
        """;

    [Fact]
    public void StyleOptions_ParsesTheEditorConfigValues()
    {
        var options = CSharpStyleLanguage.Parse(key => key switch
        {
            "csharp_prefer_braces" => "when_multiline",
            "csharp_style_namespace_declarations" => "file_scoped",
            "csharp_preferred_modifier_order" => "public, static",
            _ => null,
        });

        Assert.Equal(BracePreference.WhenMultiline, options.Braces);
        Assert.Equal(NamespacePreference.FileScoped, options.Namespaces);
        Assert.Equal(["public", "static"], options.ModifierOrder);
        Assert.True(options.AnyConfigured);
    }

    [Fact]
    public void StyleOptions_DefaultsWhenNothingIsConfigured()
    {
        var options = CSharpStyleLanguage.Parse(_ => null);

        Assert.Equal(BracePreference.None, options.Braces);
        Assert.Equal(NamespacePreference.None, options.Namespaces);
        Assert.Empty(options.ModifierOrder);
        Assert.False(options.AnyConfigured);
    }

    [Fact]
    public async Task SimplifyCode_AddsBracesOnlyWhenTheEditorConfigAsksForThem()
    {
        const string source = """
            internal class C
            {
                public int F(bool flag)
                {
                    if (flag)
                        return 1;

                    return 0;
                }
            }
            """;

        using var configured = FakeWorkspace.CreateWithEditorConfig(Braces, ("C.cs", source));
        var service = new SimplifyCodeService(configured);
        var result = await service.SimplifyAsync("document", configured.PathOf("C.cs"), null, "IDE0011", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = configured.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("if (flag)\n        {", text);
        Assert.DoesNotContain("if (flag)\n            return 1;", text);
        Assert.Contains("return 0;", text);

        // Without the option the rule has nothing to decide, and it says so instead of guessing.
        using var plain = FakeWorkspace.Create(("C.cs", source));
        var plainService = new SimplifyCodeService(plain);
        var plainResult = await plainService.SimplifyAsync("document", plain.PathOf("C.cs"), null, "IDE0011", dryRun: false);
        ResultAssert.Succeeded(plainResult);

        Assert.Equal(0, ResultAssert.Json(plainResult).GetProperty("filesChanged").GetInt32());
        Assert.Equal("not set", ResultAssert.Json(plainResult).GetProperty("styleOptions").GetProperty("braces").GetString());
        Assert.Equal(source, plain.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_UsesFileScopedNamespacesWhenTheEditorConfigAsks()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig(FileScoped, ("C.cs", """
            namespace Probe
            {
                internal class C
                {
                    public int F() => 1;
                }
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0161", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs").Replace("\r\n", "\n");
        Assert.Contains("namespace Probe;", text);
        Assert.Contains("internal class C", text);
    }

    [Fact]
    public async Task SimplifyCode_OrdersModifiersAsTheEditorConfigAsks()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig(ModifierOrder, ("C.cs", """
            internal class C
            {
                static public int F() => 1;
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0036", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public static int F()", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_AddsTheExplicitCastInForeachWhenTheEditorConfigAsks()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig(ForeachCast, ("C.cs", """
            using System.Collections.Generic;

            internal class C
            {
                public int F(List<object> values)
                {
                    foreach (string item in values)
                    {
                        return item.Length;
                    }

                    return 0;
                }
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0220", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("foreach (string item in System.Linq.Enumerable.Cast<string>(values))", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_MakesTheAccessibilityModifierExplicitWhenTheEditorConfigAsks()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig(Accessibility, ("C.cs", """
            internal class C
            {
                int F() => 1;
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0040", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("private int F() => 1;", workspace.Read("C.cs"));
        Assert.Contains("internal class C", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_FormatsTheFileOnRequest()    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
            public int F()=>1;
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0055", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public int F() => 1;", text);
        Assert.NotEqual("""
            internal class C
            {
            public int F()=>1;
            }
            """, text);
    }

    [Fact]
    public async Task SimplifyCode_FormattingAnAlreadyFormattedFileChangesNothing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                public int F() => 1;
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0055", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Equal(0, ResultAssert.Json(result).GetProperty("filesChanged").GetInt32());
    }
}
