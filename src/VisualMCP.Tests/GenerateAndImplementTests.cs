using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The "create" family: stubs for the members an interface or abstract class still owes, doc comments
/// derived from the names, and the member/type a call site assumes exists. Every write goes through
/// the compiler gate, so a wrong guess is refused rather than written.
/// </summary>
public sealed class GenerateAndImplementTests
{
    private const string Greeter = """
        public interface IGreeter
        {
            string Greet(int times);
            int Count { get; }
        }

        public class Greeter : IGreeter
        {
        }
        """;

    [Fact]
    public async Task Implement_members_creates_a_stub_for_every_missing_member()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", Greeter));

        var result = await new ImplementMembersService(workspace).ImplementMembers(
            workspace.PathOf("Greeter.cs"), "Greeter", dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("stubCount").GetInt32());

        var text = workspace.Read("Greeter.cs");
        Assert.Contains("public string Greet(int times)", text);
        Assert.Contains("public int Count", text);
    }

    [Fact]
    public async Task Implement_members_reports_when_nothing_is_missing()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", """
            public interface IGreeter { void Greet(); }

            public class Greeter : IGreeter
            {
                public void Greet() { }
            }
            """));

        var result = await new ImplementMembersService(workspace).ImplementMembers(
            workspace.PathOf("Greeter.cs"), "Greeter", dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.Equal(0, ResultAssert.Json(result).GetProperty("implemented").GetArrayLength());
    }

    [Fact]
    public async Task Implement_members_dry_run_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("Greeter.cs", Greeter));

        var result = await new ImplementMembersService(workspace).ImplementMembers(
            workspace.PathOf("Greeter.cs"), "Greeter", dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.Equal(Greeter, workspace.Read("Greeter.cs"));
    }

    [Fact]
    public async Task Generate_docs_writes_the_summary_params_and_returns()
    {
        using var workspace = FakeWorkspace.Create(("Calc.cs", """
            public class Calculator
            {
                public int CalculateTotal(int first, int second) => first + second;
            }
            """));

        var result = await new GenerateDocsService(workspace).GenerateDocs(
            workspace.PathOf("Calc.cs"), "CalculateTotal", includeMembers: false, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Calc.cs");
        Assert.Contains("/// <summary>", text);
        Assert.Contains("Calculates total.", text);
        Assert.Contains("<param name=\"first\">The first.</param>", text);
        Assert.Contains("<returns>The result.</returns>", text);
    }

    [Fact]
    public async Task Generate_docs_skips_a_member_that_already_has_one()
    {
        using var workspace = FakeWorkspace.Create(("Calc.cs", """
            public class Calculator
            {
                /// <summary>Already documented.</summary>
                public int Value { get; set; }
            }
            """));

        var result = await new GenerateDocsService(workspace).GenerateDocs(
            workspace.PathOf("Calc.cs"), "Value", includeMembers: false, dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.Equal(0, ResultAssert.Json(result).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Create_from_usage_adds_the_method_the_call_assumes()
    {
        using var workspace = FakeWorkspace.Create(("Service.cs", """
            public class Service
            {
                public void Run() => this.Write("hello", 42);
            }
            """));

        var result = await new CreateFromUsageService(workspace).CreateFromUsage(
            workspace.PathOf("Service.cs"), line: 3, memberName: "Write", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Equal("Write", ResultAssert.Json(result).GetProperty("created").GetString());

        var text = workspace.Read("Service.cs");
        Assert.True(text.Contains("public void Write(string arg1, int arg2)"), text);
    }

    [Fact]
    public async Task Create_from_usage_creates_the_missing_type()
    {
        using var workspace = FakeWorkspace.Create(("Service.cs", """
            public class Service
            {
                public MissingThing Create() => new MissingThing();
            }
            """));

        var result = await new CreateFromUsageService(workspace).CreateFromUsage(
            workspace.PathOf("Service.cs"), line: 3, memberName: "MissingThing", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Equal("MissingThing", ResultAssert.Json(result).GetProperty("created").GetString());
        Assert.Contains("public sealed class MissingThing", workspace.Read("Service.cs"));
    }

    [Fact]
    public async Task Create_from_usage_reports_a_line_it_cannot_help_with()
    {
        using var workspace = FakeWorkspace.Create(("Service.cs", """
            public class Service
            {
                public int Value = 1;
            }
            """));

        var result = await new CreateFromUsageService(workspace).CreateFromUsage(
            workspace.PathOf("Service.cs"), line: 3, memberName: null, dryRun: true);

        Assert.Contains("No compiler error", ResultAssert.Error(result));
    }
}
