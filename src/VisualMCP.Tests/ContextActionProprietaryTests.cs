using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The proprietary tail of the context-action catalogue: the static-member import family, the string
/// split/join pair, the quote wrap, the span guard and the two body-shape conversions. Each is asserted on
/// the file the action wrote, and each result must compile (the action refuses otherwise).
/// </summary>
public sealed class ContextActionProprietaryTests
{
    [Fact]
    public async Task ImportStaticMemberAddsTheUsing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public double M() => System.Math.Abs(-1.0);
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("import_static_member", workspace.PathOf("C.cs"), 5, text: "System.Math", dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.StartsWith("using static System.Math;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task QualifyMemberAddsTheTypeQualifier()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using static System.Math;

            namespace Probe;

            internal class C
            {
                public double M() => Abs(-1.0);
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("qualify_member", workspace.PathOf("C.cs"), 7, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("=> System.Math.Abs(-1.0);", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task UnqualifyMemberDropsTheTypeAndImportsIt()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public double M() => System.Math.Abs(-1.0);
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("unqualify_member", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("C.cs");
        Assert.Contains("using static System.Math;", text);
        Assert.Contains("=> Abs(-1.0);", text);
    }

    [Fact]
    public async Task SplitStringBreaksAtTheSeparator()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string M() => "one two three";
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("split_string", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("C.cs");
        Assert.Contains("\"one \"", text);
        Assert.Contains("\"two \"", text);
        Assert.Contains("\"three\"", text);
        Assert.Contains("+", text);
    }

    [Fact]
    public async Task JoinStringFoldsTheConcatenation()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string M() => "a" + "b" + "c";
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("join_string", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("=> \"abc\";", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task CheckSpanIndexInsertsTheBoundsGuard()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System;

            namespace Probe;

            internal class C
            {
                public int M(Span<int> span, int i)
                {
                    return span[i];
                }
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("check_span_index", workspace.PathOf("C.cs"), 9, dryRun: false);

        ResultAssert.Succeeded(result);
        var text = workspace.Read("C.cs");
        Assert.Contains("if (i < 0 || i >= span.Length)", text);
        Assert.Contains("throw new System.ArgumentOutOfRangeException(nameof(i), i,", text);
    }

    [Fact]
    public async Task ABlockBodyBecomesAnExpressionBody()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M(int value)
                {
                    return value + 1;
                }
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("block_body_to_expression", workspace.PathOf("C.cs"), 6, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("=> value + 1;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task AnExpressionBodyBecomesABlock()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M(int value) => value + 1;
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("expression_body_to_block", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("return value + 1;", workspace.Read("C.cs"));
    }

    [Fact]
    public void TheTailIsInTheCatalogue()
    {
        foreach (var id in new[]
                 {
                     "import_static_member", "qualify_member", "unqualify_member", "wrap_in_quotes",
                     "split_string", "join_string", "check_span_index",
                     "block_body_to_expression", "expression_body_to_block",
                 })
        {
            Assert.Contains(ContextActionService.Catalogue, entry => entry.Id == id);
        }
    }
}
