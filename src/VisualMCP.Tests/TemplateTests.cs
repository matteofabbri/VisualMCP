using Snitch.Languages.CSharp.Generation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The template family: a live template replaces its shortcut line or is inserted after the anchor, a
/// postfix template expands the expression before the dot, a file template creates a document behind the
/// compiler gate, and the custom store is written and read back.
/// </summary>
public sealed class TemplateTests
{
    private const string Class = """
        internal class C
        {
            public void M()
            {
            }
        }
        """;

    [Fact]
    public async Task LiveTemplate_IsInsertedAfterTheAnchorLine()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.ExpandAsync(
            workspace.PathOf("C.cs"), 2, "prop", "type=int;name=Count", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public int Count { get; set; }", workspace.Read("C.cs"));
        Assert.Equal("insert", ResultAssert.Json(result).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task LiveTemplate_ReplacesTheShortcutLine()
    {
        const string source = """
            internal class C
            {
                public void M()
                {
                    for
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.ExpandAsync(
            workspace.PathOf("C.cs"), 5, "for", "count=3", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("for (var i = 0; i < 3; i++)", text);
        Assert.DoesNotContain("                    for\n", text);
        Assert.Equal("replace", ResultAssert.Json(result).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task PostfixTemplate_ExpandsTheExpressionBeforeTheDot()
    {
        const string source = """
            internal class C
            {
                public void M(object? value)
                {
                    value.null
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.ExpandAsync(workspace.PathOf("C.cs"), 5, "null", null, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("value ?? throw new System.ArgumentNullException(nameof(value));", text);
        Assert.DoesNotContain("value.null", text);
    }

    [Fact]
    public async Task PostfixVar_NamesTheVariableFromTheCall()
    {
        const string source = """
            internal class C
            {
                private static System.Collections.Generic.List<int> GetOrders() => new();

                public void M()
                {
                    GetOrders().var
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.ExpandAsync(workspace.PathOf("C.cs"), 7, "var", null, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("var orders = GetOrders();", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task PostfixTemplate_RefusesALineWithoutTheDot()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.ExpandAsync(workspace.PathOf("C.cs"), 3, "null", null, dryRun: true);
        Assert.Contains("needs the line to end with", ResultAssert.Error(result));
    }

    [Fact]
    public async Task FileTemplate_CreatesTheDocument()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.CreateFileAsync(
            "class", "Invoice", null, "Demo", null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var file = Path.Combine(workspace.RootDirectory, "Invoice.cs");
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        Assert.Contains("namespace Demo;", text);
        Assert.Contains("public sealed class Invoice", text);
    }

    [Fact]
    public async Task FileTemplate_IsRefusedWhenTheProjectCannotCompileIt()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        // The fake project has no xunit reference, so the generated [Fact] must be refused, not written.
        var result = await templates.CreateFileAsync(
            "xunit-test", "Probe", null, "Demo", null, null, dryRun: false);

        Assert.Contains("Refused", ResultAssert.Error(result));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "ProbeTests.cs")));
    }

    [Fact]
    public async Task AFileTemplateCannotBeExpanded()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.ExpandAsync(workspace.PathOf("C.cs"), 2, "class", null, dryRun: true);
        Assert.Contains("file template", ResultAssert.Error(result));
    }

    [Fact]
    public async Task CustomTemplates_AreSavedListedUsedAndDeleted()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        ResultAssert.Succeeded(await templates.SaveAsync(
            "dto", "live", "public sealed record $name$($parameters$);", "A record", dryRun: false));

        var store = Path.Combine(workspace.RootDirectory, ".visualMCP", "templates.json");
        Assert.True(File.Exists(store));

        var list = ResultAssert.Json(await templates.ListAsync());
        Assert.Contains(list.GetProperty("live").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == "dto" &&
                     entry.GetProperty("custom").GetBoolean());

        var expanded = await templates.ExpandAsync(
            workspace.PathOf("C.cs"), 2, "dto", "name=Point;parameters=int X", dryRun: false);
        ResultAssert.Succeeded(expanded);
        Assert.Contains("public sealed record Point(int X);", workspace.Read("C.cs"));

        ResultAssert.Succeeded(await templates.DeleteAsync("dto", dryRun: false));

        var after = ResultAssert.Json(await templates.ListAsync());
        Assert.DoesNotContain(after.GetProperty("live").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == "dto");
    }

    [Fact]
    public async Task ACustomTemplateCannotShadowABuiltIn()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var result = await templates.SaveAsync("prop", "live", "public int X;", null, dryRun: true);
        Assert.Contains("built-in", ResultAssert.Error(result));
    }

    [Fact]
    public async Task BuiltInsAreListedInEveryKind()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Class));
        using var scope = workspace;
        var templates = new TemplateService(workspace);

        var list = ResultAssert.Json(await templates.ListAsync());

        Assert.Contains(list.GetProperty("live").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "foreach");
        Assert.Contains(list.GetProperty("postfix").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "await");
        Assert.Contains(list.GetProperty("file").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "interface");
    }
}
