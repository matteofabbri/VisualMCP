using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The two directions of the abstract-class ↔ interface conversion: the declaration and every implementer
/// are rewritten together, and the cases an interface or a class cannot express are refused by name.
/// </summary>
public sealed class ConvertTypeKindTests
{
    [Fact]
    public async Task An_abstract_class_becomes_an_interface_and_its_derived_type_drops_override()
    {
        using var workspace = FakeWorkspace.Create(
            ("Shape.cs", """
                namespace Probe;

                public abstract class Shape
                {
                    public abstract double Area();
                    public abstract string Name { get; }
                }
                """),
            ("Circle.cs", """
                namespace Probe;

                public sealed class Circle : Shape
                {
                    public override double Area() => 3.14;
                    public override string Name => "circle";
                }
                """));

        var result = await new ConvertTypeKindService(workspace)
            .ConvertTypeKind(workspace.PathOf("Shape.cs"), "Shape", "interface", dryRun: false);
        ResultAssert.Succeeded(result);

        var converted = workspace.Read("Shape.cs");
        Assert.Contains("public interface Shape", converted);
        Assert.Contains("double Area();", converted);
        Assert.Contains("string Name { get; }", converted);
        Assert.DoesNotContain("abstract", converted);

        var derived = workspace.Read("Circle.cs");
        Assert.Contains("public double Area()", derived);
        Assert.DoesNotContain("override", derived);
    }

    [Fact]
    public async Task An_interface_becomes_an_abstract_class_and_its_implementer_gains_override()
    {
        using var workspace = FakeWorkspace.Create(
            ("IShape.cs", """
                namespace Probe;

                public interface IShape
                {
                    double Area();
                    string Name { get; }
                }
                """),
            ("Circle.cs", """
                namespace Probe;

                public sealed class Circle : IShape
                {
                    public double Area() => 3.14;
                    public string Name => "circle";
                }
                """));

        var result = await new ConvertTypeKindService(workspace)
            .ConvertTypeKind(workspace.PathOf("IShape.cs"), "IShape", "abstract-class", dryRun: false);
        ResultAssert.Succeeded(result);

        var converted = workspace.Read("IShape.cs");
        Assert.Contains("public abstract class IShape", converted);
        Assert.Contains("public abstract double Area();", converted);
        Assert.Contains("public abstract string Name { get; }", converted);

        var implementer = workspace.Read("Circle.cs");
        Assert.Contains("public override double Area()", implementer);
        Assert.Contains("public override string Name", implementer);
    }

    [Fact]
    public async Task An_abstract_class_with_instance_state_is_refused()
    {
        using var workspace = FakeWorkspace.Create(("Shape.cs", """
            namespace Probe;

            public abstract class Shape
            {
                protected int count;
                public abstract double Area();
            }
            """));

        var result = await new ConvertTypeKindService(workspace)
            .ConvertTypeKind(workspace.PathOf("Shape.cs"), "Shape", "interface", dryRun: false);

        Assert.Contains("instance state", ResultAssert.Error(result));
        Assert.Contains("public abstract class Shape", workspace.Read("Shape.cs"));
    }

    [Fact]
    public async Task An_interface_implemented_by_a_struct_is_refused()
    {
        using var workspace = FakeWorkspace.Create(
            ("IShape.cs", """
                namespace Probe;

                public interface IShape
                {
                    double Area();
                }
                """),
            ("Point.cs", """
                namespace Probe;

                public struct Point : IShape
                {
                    public double Area() => 0;
                }
                """));

        var result = await new ConvertTypeKindService(workspace)
            .ConvertTypeKind(workspace.PathOf("IShape.cs"), "IShape", "abstract-class", dryRun: false);

        Assert.Contains("struct", ResultAssert.Error(result));
        Assert.Contains("public interface IShape", workspace.Read("IShape.cs"));
    }

    [Fact]
    public async Task A_dry_run_reports_the_files_and_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("Shape.cs", """
            namespace Probe;

            public abstract class Shape
            {
                public abstract double Area();
            }
            """));

        var result = await new ConvertTypeKindService(workspace)
            .ConvertTypeKind(workspace.PathOf("Shape.cs"), "Shape", "interface", dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.Contains("public abstract class Shape", workspace.Read("Shape.cs"));
    }
}
