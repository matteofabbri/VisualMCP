using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Generation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The generation and analysis tail of §5: a DTO from an XML sample, a comparer from a type's members, and
/// the regex validator. Each is asserted on what it produced, and each generator is compiler-gated.
/// </summary>
public sealed class GenerationExtrasTests
{
    [Fact]
    public async Task AnXmlSampleBecomesADtoWithAListForTheRepeatedElement()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe;\n\ninternal class C { }\n"));
        var service = new XmlDtoService(workspace);

        var result = await service.GenerateAsync(
            """
            <order id="A-1">
              <item><sku>A</sku><qty>2</qty></item>
              <item><sku>B</sku><qty>3</qty></item>
            </order>
            """,
            typeName: "Order", projectName: "Probe", namespaceName: "Probe", directory: null, dryRun: false);

        Assert.Null(result.Error);
        Assert.Equal("Order", result.Generated);
        Assert.True(File.Exists(result.File));

        var text = await File.ReadAllTextAsync(result.File!);
        Assert.Contains("public sealed class Order", text);
        Assert.Contains("public string Id { get; init; }", text);
        Assert.Contains("public List<Item> Item { get; init; } = new();", text);
        Assert.Contains("public int Qty { get; init; }", text);
    }

    [Fact]
    public async Task TheSameSampleTwiceIsRefusedTheSecondTime()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe;\n\ninternal class C { }\n"));
        var service = new XmlDtoService(workspace);

        var first = await service.GenerateAsync(
            "<payment><amount currency=\"EUR\">12.50</amount></payment>",
            typeName: "Payment", projectName: "Probe", namespaceName: "Probe", directory: null, dryRun: false);

        Assert.Null(first.Error);

        var second = await service.GenerateAsync(
            "<x/>", typeName: "Payment", projectName: "Probe", namespaceName: "Probe", directory: null, dryRun: false);

        Assert.Contains("already exists", second.Error);
    }

    [Fact]
    public async Task ATypeBecomesAnIComparerOverItsComparableProperties()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal sealed class Person
            {
                public string Name { get; init; } = string.Empty;
                public int Age { get; init; }
            }
            """));

        var result = await new ComparerGeneratorService(workspace)
            .GenerateAsync("Person", "Probe", directory: null, dryRun: false);

        Assert.Null(result.Error);
        Assert.Equal("PersonComparer", result.Generated);
        Assert.Equal(new[] { "Name", "Age" }, result.Keys);

        var text = await File.ReadAllTextAsync(result.File!);
        Assert.Contains("internal sealed class PersonComparer : System.Collections.Generic.IComparer<Person>", text);
        Assert.Contains("public static readonly PersonComparer Default = new();", text);
        Assert.Contains("byName", text);
        Assert.Contains("byAge", text);
    }

    [Fact]
    public async Task ATypeWithNothingComparableIsRefused()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal sealed class Opaque
            {
                public object? Payload { get; init; }
            }
            """));

        var result = await new ComparerGeneratorService(workspace)
            .GenerateAsync("Opaque", "Probe", directory: null, dryRun: true);

        Assert.Contains("no public comparable property", result.Error);
    }

    [Fact]
    public async Task APatternTheDotnetParserRejectsIsReported()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Text.RegularExpressions;

            namespace Probe;

            internal class C
            {
                public bool M(string value) => new Regex("([a-z").IsMatch(value);

                public bool N(string value) => Regex.IsMatch(value, "^[0-9]+$");
            }
            """));

        var result = await new RegexValidationService(workspace).FindAsync(null, 200);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Patterns);
        Assert.Equal(1, result.Invalid);

        var finding = Assert.Single(result.Findings!);
        Assert.Equal("([a-z", finding.Pattern);
        Assert.Equal(7, finding.Line);
    }

    [Fact]
    public async Task AValidPatternIsNotReported()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Text.RegularExpressions;

            namespace Probe;

            internal class C
            {
                public bool M(string value) => Regex.IsMatch(value, "^[0-9]+$");
            }
            """));

        var result = await new RegexValidationService(workspace).FindAsync(null, 200);

        Assert.Equal(1, result.Patterns);
        Assert.Equal(0, result.Invalid);
        Assert.Empty(result.Findings!);
    }
}
