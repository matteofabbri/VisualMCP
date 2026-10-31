using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Diagrams;
using Snitch.Languages.CSharp.Navigation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Diagrams and exploration: the type-level dependency graph, the call graph and sequence diagram, the
/// control-flow graph, value tracking and the localization report.
/// </summary>
public sealed class DiagramAndExplorationTests
{
    [Fact]
    public async Task The_dependency_graph_has_a_type_level()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Order
            {
                public Customer? Owner { get; set; }
            }

            internal class Customer
            {
                public string Name { get; set; } = "";
            }
            """));

        var result = ResultAssert.Json(
            await new ExportDependencyGraphService(workspace).Export("mermaid", "types"));

        Assert.Equal("types", result.GetProperty("level").GetString());
        Assert.True(result.GetProperty("nodeCount").GetInt32() >= 2);
        Assert.Equal(1, result.GetProperty("edgeCount").GetInt32());
        var graph = result.GetProperty("graph").GetString()!;
        Assert.Contains("Order", graph);
        Assert.Contains("Customer", graph);
        Assert.Contains("-->", graph);
    }

    [Fact]
    public async Task The_call_graph_walks_the_callees_to_the_depth()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Calculator
            {
                public int Add(int value) => Sub(value) + 1;

                private int Sub(int value) => value - 1;
            }
            """));

        var result = ResultAssert.Json(
            await new CallDiagramService(workspace).CallGraphAsync("Add", "Calculator", "outgoing", 2, "mermaid", 50));

        Assert.Equal(1, result.GetProperty("edgeCount").GetInt32());
        var graph = result.GetProperty("graph").GetString()!;
        Assert.StartsWith("flowchart LR", graph);
        Assert.Contains("Sub", graph);
    }

    [Fact]
    public async Task The_sequence_diagram_names_the_participants_and_messages()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class Calculator
            {
                public int Add(int value) => Sub(value) + 1;

                private int Sub(int value) => value - 1;
            }
            """));

        var result = ResultAssert.Json(
            await new CallDiagramService(workspace).SequenceAsync("Add", "Calculator", 2, 20));

        Assert.Equal(1, result.GetProperty("messageCount").GetInt32());
        var graph = result.GetProperty("graph").GetString()!;
        Assert.StartsWith("sequenceDiagram", graph);
        Assert.Contains("participant Calculator", graph);
        Assert.Contains("Sub()", graph);
    }

    [Fact]
    public async Task The_control_flow_graph_has_blocks_and_a_branch()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Sign(int value)
                {
                    if (value > 0) return 1;
                    return value < 0 ? -1 : 0;
                }
            }
            """));

        var result = ResultAssert.Json(
            await new ControlFlowGraphService(workspace).ExportAsync(workspace.PathOf("C.cs"), 5, format: "mermaid"));

        Assert.True(result.GetProperty("blockCount").GetInt32() >= 3);
        Assert.True(result.GetProperty("edgeCount").GetInt32() >= 2);
        Assert.Contains("flowchart TD", result.GetProperty("graph").GetString());
    }

    [Fact]
    public async Task Value_tracking_splits_the_writes_from_the_reads()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    var total = 0;
                    total = total + 1;
                    System.Console.WriteLine(total);
                }
            }
            """));

        var result = ResultAssert.Json(
            await new ValueTrackingService(workspace).TrackAsync(workspace.PathOf("C.cs"), 9, "total"));

        Assert.Equal("Local", result.GetProperty("kind").GetString());
        Assert.True(result.GetProperty("definitionCount").GetInt32() >= 2);
        Assert.True(result.GetProperty("useCount").GetInt32() >= 1);

        var definitions = result.GetProperty("definitions").EnumerateArray().ToList();
        Assert.Contains(definitions, definition =>
            definition.GetProperty("line").GetInt32() == 8 &&
            definition.GetProperty("snippet").GetString()!.Contains("total = total + 1"));
    }

    [Fact]
    public async Task The_localization_report_finds_unused_keys_missing_translations_and_literal_text()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Resources;

            namespace Probe;

            internal class C
            {
                public string Greeting(ResourceManager resources) =>
                    resources.GetString("Greeting") ?? "This is a user facing message";
            }
            """));

        await File.WriteAllTextAsync(Path.Combine(workspace.RootDirectory, "Strings.resx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="Greeting" xml:space="preserve"><value>Hello</value></data>
              <data name="UnusedKey" xml:space="preserve"><value>Nobody reads me</value></data>
            </root>
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootDirectory, "Strings.it.resx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="Greeting" xml:space="preserve"><value>Ciao</value></data>
            </root>
            """);

        var result = ResultAssert.Json(await new LocalizationService(workspace).ReportAsync());

        Assert.Equal(2, result.GetProperty("resourceKeyCount").GetInt32());
        Assert.Equal(1, result.GetProperty("unusedKeyCount").GetInt32());
        Assert.Equal("UnusedKey", result.GetProperty("unusedKeys")[0].GetProperty("key").GetString());

        var missing = result.GetProperty("missingTranslations")[0];
        Assert.Equal("it", missing.GetProperty("culture").GetString());
        Assert.Equal(1, missing.GetProperty("missingCount").GetInt32());
        Assert.Equal("UnusedKey", missing.GetProperty("missingKeys")[0].GetString());

        Assert.True(result.GetProperty("hardCodedCandidateCount").GetInt32() >= 1);
    }
}
