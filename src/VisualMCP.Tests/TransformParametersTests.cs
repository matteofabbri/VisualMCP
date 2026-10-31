using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// transform_parameters: the Change Signature dialog operations that are more than a reorder. Each one
/// rewrites the declaration and every call site together, and is refused when it would not compile.
/// </summary>
public sealed class TransformParametersTests
{
    [Fact]
    public async Task Make_optional_adds_a_default_and_keeps_the_call_sites()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public static string Greet(string name) => "hi " + name;

                public static string Use() => Greet("x");
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "make-optional", ["name"], defaultValue: "\"you\"", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Greeter.cs");
        Assert.Contains("public static string Greet(string name = \"you\")", text);
        Assert.Contains("Greet(\"x\")", text);
    }

    [Fact]
    public async Task Make_optional_refuses_a_required_parameter_after_an_optional_one()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public static string Greet(string name, string punctuation) => "hi " + name + punctuation;
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "make-optional", ["name"], defaultValue: "\"you\"", dryRun: false);

        Assert.Contains("required parameter after an optional one", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Make_required_refuses_when_a_call_site_omits_the_argument()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public static string Greet(string name = "you") => "hi " + name;

                public static string Use() => Greet();
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "make-required", ["name"], dryRun: false);

        Assert.Contains("omit", ResultAssert.Error(result));
        Assert.Contains("string name = \"you\"", workspace.Read("Greeter.cs"));
    }

    [Fact]
    public async Task Change_type_retypes_the_parameter_and_the_compiler_decides()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public static string Greet(string name) => "hi " + name;

                public static string Use() => Greet("x");
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "change-type", ["name"], newType: "object", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public static string Greet(object name)", workspace.Read("Greeter.cs"));
    }

    [Fact]
    public async Task Wrap_replaces_the_parameters_with_a_parameter_object()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public static string Greet(string first, string last) => first + last;

                public static string Use() => Greet("a", "b");
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "wrap", ["first", "last"], typeName: "GreetArgs", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Greeter.cs");
        Assert.Contains("public sealed class GreetArgs", text);
        Assert.Contains("public string First { get; set; }", text);
        Assert.Contains("public static string Greet(GreetArgs args) => args.First + args.Last;", text);
        Assert.Contains("Greet(new GreetArgs(\"a\", \"b\"))", text);
    }

    [Fact]
    public async Task Replace_with_property_moves_the_parameter_and_assigns_it_before_the_call()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public string Greet(string name) => "hi " + name;

                public string Use()
                {
                    return Greet("x");
                }
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "replace-with-property", ["name"], dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Greeter.cs");
        Assert.Contains("public string Name { get; set; }", text);
        Assert.Contains("public string Greet() => \"hi \" + this.Name;", text);
        Assert.Contains("this.Name = \"x\";", text);
        Assert.Contains("return Greet();", text);
    }

    [Fact]
    public async Task An_unknown_parameter_is_refused_with_the_parameter_list()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            namespace Probe;

            public class Greeter
            {
                public static string Greet(string name) => name;
            }
            """));

        var result = await new TransformParametersService(workspace)
            .TransformParameters("Greet", "make-optional", ["missing"], defaultValue: "\"x\"", dryRun: false);

        Assert.Contains("'missing'", ResultAssert.Error(result));
        Assert.Equal(1, ResultAssert.Json(result).GetProperty("parameters").GetArrayLength());
    }
}
