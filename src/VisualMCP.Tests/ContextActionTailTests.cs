using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The tail of the context-action catalogue: the empty-string pair, an auto-property made explicit, and
/// the file header the .editorconfig names. Each is asserted on the file the action wrote.
/// </summary>
public sealed class ContextActionTailTests
{
    [Fact]
    public async Task EmptyStringBecomesStringEmpty()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string M() => "";
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("empty_string_to_string_empty", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("=> string.Empty;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task StringEmptyBecomesAnEmptyLiteral()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string M() => string.Empty;
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("string_empty_to_empty_string", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("=> \"\";", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task AnAutoPropertyBecomesExplicitOverABackingField()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Value { get; set; }
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("auto_property_to_explicit", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("C.cs");
        Assert.Contains("private int _value;", text);
        Assert.Contains("get => _value;", text);
        Assert.Contains("set => _value = value;", text);
        Assert.DoesNotContain("get; set;", text);
    }

    [Fact]
    public async Task AnAutoPropertyRefusesACollidingBackingField()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                private int _value;
                public int Value { get; set; }
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("auto_property_to_explicit", workspace.PathOf("C.cs"), 6);

        Assert.Contains("_value", ResultAssert.Error(result));
    }

    [Fact]
    public async Task TheFileHeaderComesFromTheEditorConfigTemplate()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig(
            """
            root = true

            [*.cs]
            file_header_template = // Copyright (c) {fileName}
            """,
            ("C.cs", "namespace Probe;\n\ninternal class C { }\n"));

        var result = await new ContextActionService(workspace)
            .Apply("insert_file_header", workspace.PathOf("C.cs"), 1, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.StartsWith("// Copyright (c) C.cs", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task WithoutATemplateTheHeaderActionSaysWhereTheTemplateLives()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe;\n\ninternal class C { }\n"));

        var result = await new ContextActionService(workspace)
            .Apply("insert_file_header", workspace.PathOf("C.cs"), 1);

        Assert.Contains("file_header_template", ResultAssert.Error(result));
    }

    [Fact]
    public void TheTailIsInTheCatalogue()
    {
        foreach (var id in new[]
                 {
                     "empty_string_to_string_empty", "string_empty_to_empty_string",
                     "auto_property_to_explicit", "insert_file_header",
                 })
        {
            Assert.Contains(ContextActionService.Catalogue, entry => entry.Id == id);
        }
    }
}
