using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The census tail added to context_action: the actions that were still absent from the catalogue and are
/// a deterministic edit over one document. Each is asserted on the file the action wrote.
/// </summary>
public sealed class ContextActionCoverageTests
{
    [Fact]
    public async Task AnArgumentGetsItsParameterName()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void M() { N(1, 2); }
                private void N(int x, int y) { }
            }
            """));

        var result = await new ContextActionService(workspace)
            .Apply("AddArgumentNameAction", workspace.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("N(x: 1, 2)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ATupleGetsAndLosesComponentNames()
    {
        using var named = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public (int, int) M() => (1, 2);
            }
            """));

        var result = await new ContextActionService(named)
            .Apply("add_tuple_component_name", named.PathOf("C.cs"), 5, text: "left, right", dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("(int left, int right) M()", named.Read("C.cs"));

        using var unnamed = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public (int left, int right) M() => (1, 2);
            }
            """));

        var remove = await new ContextActionService(unnamed)
            .Apply("remove_tuple_component_name", unnamed.PathOf("C.cs"), 5, dryRun: false);

        ResultAssert.Succeeded(remove);
        Assert.Contains("(int, int) M()", unnamed.Read("C.cs"));
    }

    [Fact]
    public async Task ANumericLiteralChangesBase()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M() => 255;
            }
            """));

        var service = new ContextActionService(workspace);
        var hex = await service.Apply("convert_literal_to_hex", workspace.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(hex);
        Assert.Contains("0xff", workspace.Read("C.cs"));

        using var binary = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M() => 255;
            }
            """));

        var result = await new ContextActionService(binary)
            .Apply("ConvertLiteralToBinAction", binary.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(result);
        Assert.Contains("0b11111111", binary.Read("C.cs"));
    }

    [Fact]
    public async Task TargetTypedNewGoesBothWays()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Box { }

            internal class C
            {
                public void M() { Box value = new Box(); }
            }
            """));

        var service = new ContextActionService(workspace);
        var implicitNew = await service.Apply("use_target_typed_new", workspace.PathOf("C.cs"), 7, dryRun: false);
        ResultAssert.Succeeded(implicitNew);
        Assert.Contains("Box value = new();", workspace.Read("C.cs"));

        var explicitNew = await service.Apply("use_explicitly_typed_new", workspace.PathOf("C.cs"), 7, dryRun: false);
        ResultAssert.Succeeded(explicitNew);
        Assert.Contains("Box value = new Box();", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task RelationalTernaryAndIfShapesAreRewritten()
    {
        using var relational = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public bool M(int a, int b) => a < b;
            }
            """));

        var flipped = await new ContextActionService(relational)
            .Apply("FlipRelationalExpressionAction", relational.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(flipped);
        Assert.Contains("b > a", relational.Read("C.cs"));

        using var ternary = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M(bool flag) => flag ? 1 : 2;
            }
            """));

        var inverted = await new ContextActionService(ternary)
            .Apply("invert_ternary_condition", ternary.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(inverted);
        Assert.Contains("!flag ? 2 : 1", ternary.Read("C.cs"));

        using var branch = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int M(bool flag)
                {
                    if (flag) return 1; else return 2;
                }
            }
            """));

        var converted = await new ContextActionService(branch)
            .Apply("IfToTernaryAction", branch.PathOf("C.cs"), 7, dryRun: false);
        ResultAssert.Succeeded(converted);
        Assert.Contains("return flag ? 1 : 2;", branch.Read("C.cs"));
    }

    [Fact]
    public async Task DocumentationIsDerivedFromTheBaseAndFromThrows()
    {
        using var inheritdoc = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class B { public virtual void M() { } }
            internal class D : B
            {
                public override void M() { }
            }
            """));

        var inherited = await new ContextActionService(inheritdoc)
            .Apply("AddInheritdocAction", inheritdoc.PathOf("C.cs"), 6, dryRun: false);
        ResultAssert.Succeeded(inherited);
        Assert.Contains("/// <inheritdoc/>", inheritdoc.Read("C.cs"));

        using var exceptions = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void M()
                {
                    throw new System.ArgumentNullException("x");
                }
            }
            """));

        var documented = await new ContextActionService(exceptions)
            .Apply("AddExceptionDocCommentAction", exceptions.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(documented);
        Assert.Contains("""<exception cref="System.ArgumentNullException">""", exceptions.Read("C.cs"));
    }

    [Fact]
    public async Task RegionNewlineAndParamsAreEdited()
    {
        using var region = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            #region Group
            internal class C { }
            #endregion
            """));

        var removed = await new ContextActionService(region)
            .Apply("RemoveRegionAction", region.PathOf("C.cs"), 3, dryRun: false);
        ResultAssert.Succeeded(removed);
        Assert.DoesNotContain("#region", region.Read("C.cs"));

        using var newline = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string M() => "\n";
            }
            """));

        var environment = await new ContextActionService(newline)
            .Apply("use_environment_newline", newline.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(environment);
        Assert.Contains("Environment.NewLine", newline.Read("C.cs"));

        using var parameters = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void M(params int[] values) { }
            }
            """));

        var service = new ContextActionService(parameters);
        var without = await service.Apply("remove_params_modifier", parameters.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(without);
        Assert.Contains("int[] values", parameters.Read("C.cs"));

        var with = await service.Apply("set_params_modifier", parameters.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(with);
        Assert.Contains("params int[] values", parameters.Read("C.cs"));
    }

    [Fact]
    public async Task SwitchSectionBracesAndAwaitForeachAreApplied()
    {
        using var section = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void M(int value)
                {
                    switch (value)
                    {
                        case 1:
                            System.Console.WriteLine(1);
                            break;
                    }
                }
            }
            """));

        var braced = await new ContextActionService(section)
            .Apply("AddSwitchSectionBracesAction", section.PathOf("C.cs"), 10, dryRun: false);
        ResultAssert.Succeeded(braced);
        Assert.Contains("case 1:", section.Read("C.cs"));

        using var asyncLoop = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace Probe;

            internal class C
            {
                public async Task M(IAsyncEnumerable<int> xs)
                {
                    foreach (var x in xs) { }
                }
            }
            """));

        var awaited = await new ContextActionService(asyncLoop)
            .Apply("IterateViaAwaitForeachAction", asyncLoop.PathOf("C.cs"), 10, dryRun: false);
        ResultAssert.Succeeded(awaited);
        Assert.Contains("await foreach", asyncLoop.Read("C.cs"));
    }

    [Fact]
    public void TheNewActionsAreInTheCatalogue()
    {
        foreach (var id in new[]
                 {
                     "check_string_not_null_or_whitespace", "add_argument_name",
                     "add_tuple_component_name", "remove_tuple_component_name",
                     "add_switch_section_braces", "convert_literal_to_hex",
                     "convert_literal_to_binary", "convert_literal_to_decimal",
                     "use_target_typed_new", "use_explicitly_typed_new",
                     "convert_to_default_expression", "flip_relational_expression",
                     "invert_ternary_condition", "if_to_ternary", "add_inheritdoc",
                     "add_exception_docs", "remove_region", "use_environment_newline",
                     "remove_params_modifier", "set_params_modifier", "await_foreach",
                 })
        {
            Assert.Contains(ContextActionService.Catalogue, entry => entry.Id == id);
        }
    }
}
