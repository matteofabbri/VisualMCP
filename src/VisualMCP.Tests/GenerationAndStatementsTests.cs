using Snitch.Languages.CSharp.ContextActions;
using Snitch.Languages.CSharp.Generation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The generators (a DTO from JSON, a fluent builder) and the two statement context actions
/// (for→foreach, if→switch), each checked on the text they produce plus the case they refuse.
/// </summary>
public sealed class GenerationAndStatementsTests
{
    // ── generators ──────────────────────────────────────────────────────────
    [Fact]
    public async Task GenerateDtoFromJson_InfersTheShapeAndWritesTheFile()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var scope = workspace;
        var service = new DtoFromJsonService(workspace);

        const string json = """
            {
              "orderId": 42,
              "total": 19.5,
              "paid": true,
              "customer": { "name": "Ada", "email": "ada@example.com" },
              "tags": ["a", "b"]
            }
            """;

        var result = ResultAssert.Json(await service.GenerateAsync(json, "OrderDto", "Probe", null, null, dryRun: false));
        ResultAssert.Succeeded(result);

        var source = File.ReadAllText(workspace.PathOf("OrderDto.cs"));
        Assert.Contains("public sealed record OrderDto", source);
        Assert.Contains("public long OrderId", source);
        Assert.Contains("public double Total", source);
        Assert.Contains("public bool Paid", source);
        Assert.Contains("public CustomerDto Customer", source);
        Assert.Contains("public List<string> Tags", source);
        Assert.Contains("public sealed record CustomerDto", source);
    }

    [Fact]
    public async Task GenerateDtoFromJson_RefusesAJsonRootThatIsNotAnObject()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var scope = workspace;
        var service = new DtoFromJsonService(workspace);

        Assert.Contains("root must be an object", ResultAssert.Error(
            await service.GenerateAsync("[1,2]", "ItemsDto", "Probe", null, null, dryRun: true)));
    }

    [Fact]
    public async Task GenerateBuilder_WritesAWithPerProperty()
    {
        const string source = """
            namespace Probe;

            internal class Widget
            {
                public string Name { get; set; } = string.Empty;
                public int Size { get; set; }
                public string ReadOnly => Name;
            }
            """;

        var workspace = FakeWorkspace.Create(("Widget.cs", source));
        using var scope = workspace;
        var service = new BuilderGeneratorService(workspace);

        var result = ResultAssert.Json(await service.GenerateAsync("Widget", "Probe", null, dryRun: false));
        ResultAssert.Succeeded(result);

        var generated = File.ReadAllText(workspace.PathOf("WidgetBuilder.cs"));
        // The builder mirrors the type's accessibility: an internal Widget cannot be returned by a public builder.
        Assert.Contains("internal sealed class WidgetBuilder", generated);
        Assert.Contains("WithName(string value)", generated);
        Assert.Contains("WithSize(int value)", generated);
        Assert.DoesNotContain("WithReadOnly", generated);
        Assert.Contains("public Widget Build()", generated);
    }

    // ── IL view ─────────────────────────────────────────────────────────────
    [Fact]
    public void Disassemble_DecodesARealMethodBody()
    {
        var assembly = typeof(Snitch.Debugging.DapMessage).Assembly.Location;
        var service = new Snitch.Metacode.Decompilation.DisassemblyService();

        var result = ResultAssert.Json(service.Disassemble(assembly, "Snitch.Debugging.DapMessage", "BodyString", 5));

        Assert.True(result.GetProperty("returned").GetInt32() >= 1, result.GetRawText());
        var il = result.GetProperty("methods")[0].GetProperty("il").EnumerateArray()
            .Select(line => line.GetString() ?? string.Empty).ToList();

        Assert.NotEmpty(il);
        Assert.Contains(il, line => line.Contains("ret"));
        Assert.Contains(il, line => line.Contains("ldstr") || line.Contains("call") || line.Contains("brtrue"));
    }

    [Fact]
    public void Disassemble_RefusesANonManagedFile()
    {
        var service = new Snitch.Metacode.Decompilation.DisassemblyService();
        var result = ResultAssert.Json(service.Disassemble(@"C:\Windows\System32\kernel32.dll"));

        Assert.Contains("not a managed assembly", result.GetProperty("error").GetString());
    }

    // ── context actions ─────────────────────────────────────────────────────
    [Fact]
    public async Task ForToForeach_RewritesTheScan()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int Sum(int[] xs)
                {
                    var sum = 0;
                    for (var i = 0; i < xs.Length; i++)
                    {
                        sum += xs[i];
                    }

                    return sum;
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ContextActionService(workspace);

        ResultAssert.Succeeded(await service.Apply("for_to_foreach", workspace.PathOf("C.cs"), 8, null, null, dryRun: false));

        var text = workspace.Read("C.cs");
        Assert.Contains("foreach (var item in xs)", text);
        Assert.Contains("sum += item;", text);
        Assert.DoesNotContain("xs[i]", text);
    }

    [Fact]
    public async Task ForToForeach_RefusesWhenTheIndexIsUsedForSomethingElse()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int Sum(int[] xs)
                {
                    var sum = 0;
                    for (var i = 0; i < xs.Length; i++)
                    {
                        sum += xs[i] + i;
                    }

                    return sum;
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ContextActionService(workspace);

        Assert.Contains("used for something other than indexing", ResultAssert.Error(
            await service.Apply("for_to_foreach", workspace.PathOf("C.cs"), 8, null, null, dryRun: true)));
    }

    [Fact]
    public async Task IfToSwitch_TurnsTheChainIntoACasePerBranch()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int Classify(int x)
                {
                    if (x == 1) return 10;
                    else if (x == 2) return 20;
                    else return 30;
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ContextActionService(workspace);

        ResultAssert.Succeeded(await service.Apply("if_to_switch", workspace.PathOf("C.cs"), 7, null, null, dryRun: false));

        var text = workspace.Read("C.cs");
        Assert.Contains("switch (x)", text);
        Assert.Contains("case 1:", text);
        Assert.Contains("case 2:", text);
        Assert.Contains("default:", text);
        Assert.DoesNotContain("else if", text);
    }

    [Fact]
    public async Task IfToSwitch_RefusesAConditionThatIsNotAnEquality()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int Classify(int x)
                {
                    if (x > 1) return 10;
                    else if (x > 2) return 20;
                    else return 30;
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ContextActionService(workspace);

        Assert.Contains("not an equality", ResultAssert.Error(
            await service.Apply("if_to_switch", workspace.PathOf("C.cs"), 7, null, null, dryRun: true)));
    }
}
