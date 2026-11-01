using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// migration_campaign: each campaign is asserted on the file it wrote, and the parse of that file says
/// what the rewrite produced — a diff would only prove the text changed.
/// </summary>
public sealed class MigrationCampaignTests
{
    [Fact]
    public async Task The_nullable_campaign_adds_the_header()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C\n{\n    internal void M() { }\n}\n"));

        var result = await Service(workspace).RunAsync("nullable", null, dryRun: false);

        Assert.Equal(1, result.FilesChanged);
        Assert.StartsWith("#nullable enable", workspace.Read("C.cs"));
        Assert.Single(result.Changed!);
    }

    [Fact]
    public async Task A_file_that_already_declares_nullable_is_left_alone()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "#nullable disable\ninternal class C { }\n"));

        var result = await Service(workspace).RunAsync("nullable", null, dryRun: false);

        Assert.Equal(0, result.FilesChanged);
        Assert.Contains("Nothing to migrate", result.Note);
    }

    [Fact]
    public async Task A_dry_run_reports_the_change_without_writing_it()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));

        var result = await Service(workspace).RunAsync("nullable", null, dryRun: true);

        Assert.Equal(1, result.FilesChanged);
        Assert.True(result.DryRun);
        Assert.DoesNotContain("#nullable", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task The_file_scoped_campaign_converts_the_namespace()
    {
        using var workspace = FakeWorkspace.Create((
            "C.cs",
            """
            namespace Probe
            {
                internal class C
                {
                    internal void M() { }
                }
            }
            """));

        var result = await Service(workspace).RunAsync("file-scoped-namespaces", null, dryRun: false);

        Assert.Equal(1, result.FilesChanged);

        var root = Parse(workspace.Read("C.cs"));
        var fileScoped = Assert.IsType<FileScopedNamespaceDeclarationSyntax>(Assert.Single(root.Members));
        Assert.Equal("Probe", fileScoped.Name.ToString());
        Assert.Single(fileScoped.Members);
    }

    [Fact]
    public async Task A_file_with_two_namespaces_is_left_alone()
    {
        using var workspace = FakeWorkspace.Create((
            "C.cs",
            """
            namespace A
            {
                internal class One { }
            }

            namespace B
            {
                internal class Two { }
            }
            """));

        var result = await Service(workspace).RunAsync("file-scoped-namespaces", null, dryRun: false);

        Assert.Equal(0, result.FilesChanged);
        Assert.Contains("namespace A", workspace.Read("C.cs"));
        Assert.Contains("namespace B", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task The_braces_campaign_wraps_an_embedded_statement()
    {
        using var workspace = FakeWorkspace.Create((
            "C.cs",
            """
            internal class C
            {
                internal void M(bool flag)
                {
                    if (flag) return;
                    for (var i = 0; i < 3; i++) M(false);
                }
            }
            """));

        var result = await Service(workspace).RunAsync("braces", null, dryRun: false);

        Assert.Equal(1, result.FilesChanged);

        var root = Parse(workspace.Read("C.cs"));
        Assert.IsType<BlockSyntax>(root.DescendantNodes().OfType<IfStatementSyntax>().Single().Statement);
        Assert.IsType<BlockSyntax>(root.DescendantNodes().OfType<ForStatementSyntax>().Single().Statement);
    }

    [Fact]
    public async Task An_else_if_chain_is_not_wrapped_again()
    {
        using var workspace = FakeWorkspace.Create((
            "C.cs",
            """
            internal class C
            {
                internal int M(int flag)
                {
                    if (flag == 1) return 1;
                    else if (flag == 2) return 2;
                    return 0;
                }
            }
            """));

        await Service(workspace).RunAsync("braces", null, dryRun: false);

        var root = Parse(workspace.Read("C.cs"));
        var chain = root.DescendantNodes().OfType<IfStatementSyntax>().ToList();
        Assert.Equal(2, chain.Count);
        Assert.All(chain, statement => Assert.IsType<BlockSyntax>(statement.Statement));
    }

    [Fact]
    public async Task The_primary_constructor_campaign_converts_an_eligible_type()
    {
        using var workspace = FakeWorkspace.Create((
            "C.cs",
            """
            namespace Probe
            {
                internal sealed class Person
                {
                    private readonly string _name;

                    public Person(string name)
                    {
                        _name = name;
                    }

                    public string Name => _name;
                }
            }
            """));

        var result = await Service(workspace).RunAsync("primary-constructors", null, dryRun: false);

        Assert.Equal(1, result.FilesChanged);
        var root = Parse(workspace.Read("C.cs"));
        var person = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.Text == "Person");
        Assert.NotNull(person.ParameterList);
    }

    [Fact]
    public async Task An_unknown_campaign_lists_the_supported_ones()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));

        var result = await Service(workspace).RunAsync("modernize-everything", null, dryRun: true);

        Assert.NotNull(result.Error);
        Assert.Contains("file-scoped-namespaces", result.Campaigns!);
    }

    private static MigrationCampaignService Service(FakeWorkspace workspace) =>
        new(workspace, new ConvertToPrimaryConstructorService(workspace));

    private static CompilationUnitSyntax Parse(string text) =>
        CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
}
