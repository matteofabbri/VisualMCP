using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The last two refactorings Section 7 of docs/gaps.md listed as missing — a type moved into another
/// type, and generify — plus the cases of make_non_static and transform_parameters the focused test
/// files do not cover. Each test runs the real service, which gates the edit on the compiler internally.
/// </summary>
public sealed class RefactoringGapTests
{
    // ── make_non_static: a parameter is a receiver ──────────────────────────────────

    [Fact]
    public async Task MakeNonStatic_RoutesTheCallThroughAParameter()
    {
        using var workspace = FakeWorkspace.Create(
            ("Target.cs", """
                namespace Probe;

                internal class Target
                {
                    public static int Pure(int value) => value * 2;
                }
                """),
            ("Caller.cs", """
                namespace Probe;

                internal static class Caller
                {
                    public static int Run(Target target) => Target.Pure(3);
                }
                """));

        var service = new MakeNonStaticService(workspace);
        var result = await service.MakeNonStatic(workspace.PathOf("Target.cs"), "Pure", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public int Pure(int value)", workspace.Read("Target.cs"));
        Assert.Contains("target.Pure(3)", workspace.Read("Caller.cs"));
    }

    // ── transform_parameters: the default parameter-object name ─────────────────────

    [Fact]
    public async Task TransformParameters_WrapDefaultsTheClassNameToTheMethodName()
    {
        using var workspace = FakeWorkspace.Create(
            ("Calculator.cs", """
                namespace Probe;

                internal class Calculator
                {
                    public int Compute(int value, int factor)
                    {
                        return value * factor;
                    }
                }
                """));

        var service = new TransformParametersService(workspace);
        var result = await service.TransformParameters(
            "Compute", "wrap", ["value", "factor"], dryRun: false);
        ResultAssert.Succeeded(result);

        var declaration = workspace.Read("Calculator.cs");
        Assert.Contains("ComputeArguments args", declaration);
        Assert.Contains("return args.Value * args.Factor;", declaration);
        Assert.Contains("sealed class ComputeArguments", declaration);
    }

    // ── move_type_to_nested_scope ──────────────────────────────────────────────────

    [Fact]
    public async Task MoveTypeToNestedScope_MovesTheTypeAndRewritesReferences()
    {
        using var workspace = FakeWorkspace.Create(
            ("Order.cs", """
                namespace Probe;

                internal class Order
                {
                    public int Total => 1;
                }
                """),
            ("Processor.cs", """
                namespace Probe;

                internal class Processor
                {
                    public int Run() => new Order().Total;
                }
                """),
            ("Consumer.cs", """
                namespace Probe;

                internal class Consumer
                {
                    public int Read() => new Order().Total;
                }
                """));

        var service = new MoveTypeToNestedScopeService(workspace);
        var result = await service.MoveTypeToNestedScope(
            workspace.PathOf("Order.cs"), "Order", "Processor", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.False(File.Exists(workspace.PathOf("Order.cs")));

        var processor = workspace.Read("Processor.cs");
        Assert.Contains("internal class Order", processor);
        Assert.Contains("new Order().Total", processor);

        Assert.Contains("new Processor.Order().Total", workspace.Read("Consumer.cs"));
    }

    [Fact]
    public async Task MoveTypeToNestedScope_RefusesWhenTheSourceIsAlreadyNested()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Outer
            {
                internal class Inner { }
            }
            """));

        var service = new MoveTypeToNestedScopeService(workspace);
        var result = await service.MoveTypeToNestedScope(
            workspace.PathOf("C.cs"), "Inner", "Outer", dryRun: true);

        Assert.Contains("already nested", ResultAssert.Error(result));
    }

    // ── generify: a method ────────────────────────────────────────────────────────

    [Fact]
    public async Task Generify_AbstractsTheParameterAndReturnTypeOfAMethod()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string Describe(int value) => value.ToString();

                public string Use() => Describe(3);
            }
            """));

        var service = new GenerifyService(workspace);
        var result = await service.Generify(
            workspace.PathOf("C.cs"), methodName: "Describe", usages: ["int"], dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public string Describe<T>(T value)", text);
        Assert.Contains("Describe(3)", text);
    }

    [Fact]
    public async Task Generify_SuppliesAnExplicitTypeArgumentWhenInferenceImpossible()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Value() => default;

                public int Use() => Value();
            }
            """));

        var service = new GenerifyService(workspace);
        var result = await service.Generify(
            workspace.PathOf("C.cs"), methodName: "Value", usages: ["int"], dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public T Value<T>()", text);
        Assert.Contains("Value<int>()", text);
    }

    // ── generify: a type ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Generify_MakesATypeGenericAndSpellsTheArgumentsAtEveryReference()
    {
        using var workspace = FakeWorkspace.Create(
            ("Cache.cs", """
                namespace Probe;

                internal class Cache
                {
                    public int Value { get; set; }
                }
                """),
            ("Use.cs", """
                namespace Probe;

                internal class Use
                {
                    public Cache Create() => new Cache();
                }
                """));

        var service = new GenerifyService(workspace);
        var result = await service.Generify(
            workspace.PathOf("Cache.cs"), typeName: "Cache", usages: ["int"], dryRun: false);
        ResultAssert.Succeeded(result);

        var declaration = workspace.Read("Cache.cs");
        Assert.True(declaration.Contains("internal class Cache<T>"), declaration);
        Assert.True(declaration.Contains("public T Value { get; set; }"), declaration);

        var use = workspace.Read("Use.cs");
        Assert.True(use.Contains("public Cache<int> Create()"), use);
        Assert.True(use.Contains("new Cache<int>()"), use);
    }
}
