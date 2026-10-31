using Snitch.Languages.WebMarkup;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// analyze_markup: the HTML rules (accessibility and safety floor), the tag balance, and the CSS rules.
/// One case that fires and one that must not, per rule.
/// </summary>
public sealed class MarkupAnalyzerTests
{
    private static string[] HtmlRules(string source) =>
        MarkupAnalyzer.AnalyzeHtml(source, "probe.html")
            .Findings.Select(finding => finding.RuleId).Distinct().OrderBy(id => id).ToArray();

    private static string[] CssRules(string source) =>
        MarkupAnalyzer.AnalyzeCss(source, "probe.css")
            .Findings.Select(finding => finding.RuleId).Distinct().OrderBy(id => id).ToArray();

    [Fact]
    public void ACleanPageHasNoFindingsAndItsIdsAreRead()
    {
        var analysis = MarkupAnalyzer.AnalyzeHtml("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Probe</title>
            </head>
            <body>
              <img src="a.png" alt="A">
              <label for="name">Name</label>
              <input id="name" name="name" type="text">
              <button type="button">Go</button>
            </body>
            </html>
            """, "probe.html");

        Assert.Empty(analysis.Findings);
        Assert.Equal("html", analysis.Kind);
        Assert.Contains("name", analysis.Ids);
        Assert.True(analysis.Elements > 5);
    }

    [Fact]
    public void ImageAltIsRequired()
    {
        Assert.Contains("HTML0001", HtmlRules("<img src=\"a.png\">"));
        Assert.DoesNotContain("HTML0001", HtmlRules("<img src=\"a.png\" alt=\"A\">"));
    }

    [Fact]
    public void LanguageAndViewportAreRequired()
    {
        Assert.Contains("HTML0002", HtmlRules("<html><head></head><body></body></html>"));
        Assert.DoesNotContain("HTML0002", HtmlRules("<html lang=\"en\"><head></head><body></body></html>"));

        Assert.Contains("HTML0003", HtmlRules("<html lang=\"en\"><head></head><body></body></html>"));
        Assert.DoesNotContain("HTML0003", HtmlRules(
            "<html lang=\"en\"><head><meta name=\"viewport\" content=\"width=device-width\"></head><body></body></html>"));
    }

    [Fact]
    public void DuplicateIdsAreFlagged()
    {
        const string source = """
            <div id="a"></div>
            <span id="a"></span>
            <p id="b"></p>
            """;

        Assert.Contains("HTML0004", HtmlRules(source));
        Assert.DoesNotContain("HTML0004", HtmlRules("""<div id="a"></div><p id="b"></p>"""));
    }

    [Fact]
    public void ALabelMustBeAttachedToAControl()
    {
        Assert.Contains("HTML0005", HtmlRules("<label>Name</label>"));
        Assert.DoesNotContain("HTML0005", HtmlRules("<label for=\"name\">Name</label>"));
        Assert.DoesNotContain("HTML0005", HtmlRules("<label>Name <input name=\"n\"></label>"));
    }

    [Fact]
    public void NestedFormsAreAnError()
    {
        Assert.Contains("HTML0006", HtmlRules("<form><form></form></form>"));
        Assert.DoesNotContain("HTML0006", HtmlRules("<form></form><form></form>"));
    }

    [Fact]
    public void DeprecatedElementsAndInlineHandlersAreFlagged()
    {
        Assert.Contains("HTML0007", HtmlRules("<center>text</center>"));
        Assert.Contains("HTML0008", HtmlRules("""<button type="button" onclick="run()">Go</button>"""));
        Assert.DoesNotContain("HTML0008", HtmlRules("""<button type="button">Go</button>"""));
    }

    [Fact]
    public void ANeeTabLinkNeedsNoopener()
    {
        Assert.Contains("HTML0009", HtmlRules("""<a href="https://x" target="_blank">x</a>"""));
        Assert.DoesNotContain("HTML0009", HtmlRules("""<a href="https://x" target="_blank" rel="noopener">x</a>"""));
    }

    [Fact]
    public void EmptyHrefsAndTypelessButtonsAreFlagged()
    {
        Assert.Contains("HTML0010", HtmlRules("""<a href="#">x</a>"""));
        Assert.DoesNotContain("HTML0010", HtmlRules("""<a href="https://x">x</a>"""));
        Assert.Contains("HTML0011", HtmlRules("<button>Go</button>"));
        Assert.DoesNotContain("HTML0011", HtmlRules("""<button type="button">Go</button>"""));
    }

    [Fact]
    public void AFormControlWithoutANameIsFlagged()
    {
        Assert.Contains("HTML0012", HtmlRules("""<input type="text">"""));
        Assert.DoesNotContain("HTML0012", HtmlRules("""<input type="text" name="q">"""));
        Assert.DoesNotContain("HTML0012", HtmlRules("""<input type="submit">"""));
    }

    [Fact]
    public void ASubresourceOverHttpIsFlagged()
    {
        Assert.Contains("HTML0013", HtmlRules("""<script src="http://cdn/x.js"></script>"""));
        Assert.DoesNotContain("HTML0013", HtmlRules("""<script src="https://cdn/x.js"></script>"""));
        Assert.DoesNotContain("HTML0013", HtmlRules("""<a href="http://example.com">x</a>"""));
    }

    [Fact]
    public void UnclosedTagsAreReportedAndBalancedOnesAreNot()
    {
        Assert.Contains("HTML0014", HtmlRules("<div><span></span>"));
        Assert.DoesNotContain("HTML0014", HtmlRules("<div><span></span></div>"));
        Assert.DoesNotContain("HTML0014", HtmlRules("<br><img src=\"a\"><input name=\"n\">"));
    }

    [Fact]
    public void ScriptAndStyleBodiesAreNotParsedAsMarkup()
    {
        Assert.DoesNotContain("HTML0014", HtmlRules(
            """<div><script>const html = "<div>";</script></div>"""));
        Assert.DoesNotContain("HTML0001", HtmlRules(
            """<script>document.body.innerHTML = "<img src='a'>";</script>"""));
    }

    [Fact]
    public void ACleanStylesheetHasNoFindings()
    {
        Assert.Empty(CssRules("""
            .card { color: red; }
            .card:hover { color: blue; }
            @media (min-width: 40rem) { .card { padding: 1rem; } }
            """));
    }

    [Fact]
    public void TheStylesheetRulesFire()
    {
        Assert.Contains("CSS0001", CssRules(".card { color: red !important; }"));
        Assert.Contains("CSS0002", CssRules("#main { color: red; }"));
        Assert.Contains("CSS0003", CssRules(".card { }"));
        Assert.Contains("CSS0004", CssRules("@import \"other.css\";"));
        Assert.Contains("CSS0007", CssRules(".card { background: url(javascript:alert(1)); }"));
        Assert.Contains("CSS0008", CssRules(".card { width: expression(alert(1)); }"));
    }

    [Fact]
    public void ADuplicateSelectorIsFlaggedOnce()
    {
        Assert.Contains("CSS0005", CssRules(".card { color: red; }\n.card { color: blue; }"));
        Assert.DoesNotContain("CSS0005", CssRules(".card { color: red; }\n.title { color: blue; }"));
    }

    [Fact]
    public void UnbalancedBracesAreFlagged()
    {
        Assert.Contains("CSS0006", CssRules(".card { color: red;"));
        Assert.DoesNotContain("CSS0006", CssRules(".card { color: red; }"));
    }

    [Fact]
    public void ACommentIsNotACssRule()
    {
        Assert.DoesNotContain("CSS0001", CssRules("/* .card { color: red !important; } */\n.card { color: red; }"));
    }

    [Fact]
    public async Task TheServiceScansTheTreeSkipsBuildOutputAndFiltersByRule()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);

        var root = workspace.RootDirectory;
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        Directory.CreateDirectory(Path.Combine(root, "dist"));
        await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "index.html"),
            """<html lang="en"><head><meta name="viewport" content="width=device-width"></head><body><img src="a"></body></html>""");
        await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "site.css"), ".a { color: red !important; }");
        await File.WriteAllTextAsync(Path.Combine(root, "dist", "ignored.html"), "<div>");

        var inventory = ResultAssert.Json(await service.InventoryAsync(root, "both", 100));
        Assert.Equal(2, inventory.GetProperty("fileCount").GetInt32());

        var html = ResultAssert.Json(await service.AnalyzeAsync(root, "html", "info", "HTML0001", 100, 100));
        Assert.Equal(1, html.GetProperty("findingCount").GetInt32());
        Assert.Equal(1, html.GetProperty("filesAnalyzed").GetInt32());

        var unknown = ResultAssert.Error(await service.AnalyzeAsync(root, "both", "info", "HTML9999", 100, 100));
        Assert.Contains("Unknown rule", unknown);

        var badKind = ResultAssert.Error(await service.AnalyzeAsync(root, "yaml", "info", null, 100, 100));
        Assert.Contains("kind must be", badKind);
    }
}
