using VisualMCP.Analysis;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// One catalogue answers two questions — what the server advertises (capability_report) and what a board
/// card may name (board_audit) — so the two can never disagree. The snake_case half matters because the
/// MCP host derives a tool's wire name from its method when the attribute declares none.
/// </summary>
public sealed class ToolCatalogueTests
{
    [Theory]
    [InlineData("FindSymbol", "find_symbol")]
    [InlineData("SignalRConnect", "signal_r_connect")]
    [InlineData("GetXmlDocs", "get_xml_docs")]
    [InlineData("AnalyzeRazorPages", "analyze_razor_pages")]
    [InlineData("RazorPages", "razor_pages")]
    [InlineData("RunTests", "run_tests")]
    public void SnakeCase_MatchesTheNameTheClientCalls(string method, string wire)
        => Assert.Equal(wire, ToolCatalogue.SnakeCase(method));

    [Fact]
    public void TheCatalogueKnowsBothSpellingsOfAToolNamedAfterItsMethod()
    {
        var names = ToolCatalogue.ToolNames();

        Assert.Contains("find_symbol", names);
        Assert.Contains("read_file", names);
        Assert.Contains("board_audit", names);
    }

    [Fact]
    public void TheKnownNamesIncludeTheContextActionsThatAreNotTools()
    {
        var known = ToolCatalogue.KnownNames();

        // foreach_to_for is an id inside context_action, not a tool of its own.
        Assert.DoesNotContain("foreach_to_for", ToolCatalogue.ToolNames());
        Assert.Contains("foreach_to_for", known);
        Assert.Contains("context_action", known);
    }

    [Fact]
    public void EveryAreaComesFromTheToolNamespace()
    {
        var entries = ToolCatalogue.Entries();

        Assert.Contains(entries, entry => entry.Area == "Planning" && entry.Name == "board_brief");
        Assert.DoesNotContain(entries, entry => entry.Area == "other");
    }
}
