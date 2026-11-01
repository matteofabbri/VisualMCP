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

    // ── the tag/class/id index and the simple-selector match ─────────────────

    [Fact]
    public void ThePageIndexReadsTagsClassesAndIds()
    {
        var analysis = MarkupAnalyzer.AnalyzeHtml(
            """<div id="main" class="card wide"><span class="badge">x</span></div>""", "probe.html");

        Assert.NotNull(analysis.Nodes);
        Assert.Equal(2, analysis.Nodes.Count);

        var div = analysis.Nodes[0];
        Assert.Equal("div", div.Tag);
        Assert.Equal("main", div.Id);
        Assert.Contains("card", div.Classes);
        Assert.Contains("wide", div.Classes);

        Assert.Equal("span", analysis.Nodes[1].Tag);
        Assert.Null(analysis.Nodes[1].Id);
    }

    [Fact]
    public void SelectorsAreIndexedWithTheirCombinatorsAttributesAndPseudoClasses()
    {
        var analysis = MarkupAnalyzer.AnalyzeCss("""
            .card { color: red; }
            div.card { color: blue; }
            #main { color: green; }
            .a .b { color: black; }
            .a:hover { color: grey; }
            [data-x] { color: white; }
            ul > li:first-child { color: pink; }
            """, "probe.css");

        var byText = analysis.Selectors!.ToDictionary(selector => selector.Text);

        Assert.True(byText["div.card"].Checkable);
        Assert.Equal("card", byText["div.card"].Complex!.Parts[^1].Classes!.Single());
        Assert.Equal(CssCombinator.Descendant, byText[".a .b"].Complex!.Parts[1].Combinator);
        Assert.Equal(CssCombinator.Child, byText["ul > li:first-child"].Complex!.Parts[1].Combinator);
        Assert.Equal("first-child", byText["ul > li:first-child"].Complex!.Parts[1].PseudoClasses!.Single());

        // A dynamic state only a browser can decide: indexed (for its classes) but never reported.
        Assert.False(byText[".a:hover"].Checkable);
        Assert.True(byText["[data-x]"].Checkable);

        var classes = analysis.Classes!;
        Assert.Contains("a", classes);
        Assert.Contains("b", classes);
        Assert.Contains("card", classes);
    }

    [Fact]
    public async Task ASelectorThatMatchesNothingIsReported()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"),
            """<div class="card" id="main"></div>""");
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), """
            .card { color: red; }
            .missing { color: blue; }
            #nope { color: green; }
            section { color: black; }
            .card .missing { color: grey; }
            """);

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "both", "info", "CSS0009", 100, 100));
        var snippets = json.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("Snippet").GetString())
            .ToList();

        Assert.Equal(4, json.GetProperty("findingCount").GetInt32());
        Assert.Contains(".missing", snippets);
        Assert.Contains("#nope", snippets);
        Assert.Contains("section", snippets);
        Assert.Contains(".card .missing", snippets);
        Assert.DoesNotContain(".card", snippets);
    }

    [Fact]
    public async Task CombinatorsAttributesAndStructuralPseudoClassesResolveAgainstThePage()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """
            <ul class="menu"><li class="item"><a class="link" href="/home">home</a></li></ul>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), """
            .menu > li.item { color: red; }
            .menu .link { color: blue; }
            .link[href="/home"] { color: green; }
            li:first-child { color: black; }
            .menu > a { color: grey; }
            .menu + p { color: white; }
            [href^="/away"] { color: pink; }
            .menu:hover { color: teal; }
            """);

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "both", "info", "CSS0009", 100, 100));
        var snippets = json.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("Snippet").GetString())
            .ToList();

        Assert.Contains(".menu > a", snippets);      // the link is a grandchild, not a child
        Assert.Contains(".menu + p", snippets);      // there is no following sibling
        Assert.Contains("[href^=\"/away\"]", snippets);

        Assert.DoesNotContain(".menu > li.item", snippets);
        Assert.DoesNotContain(".menu .link", snippets);
        Assert.DoesNotContain(".link[href=\"/home\"]", snippets);
        Assert.DoesNotContain("li:first-child", snippets);

        // A selector with a state only the browser knows is never reported as unmatched.
        Assert.DoesNotContain(".menu:hover", snippets);
    }

    [Fact]
    public async Task AClassUsedButNeverDefinedIsReported()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"),
            """<div class="hero card"></div>""");
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), ".card { color: red; }");

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "both", "info", "HTML0015", 100, 100));
        var finding = Assert.Single(json.GetProperty("findings").EnumerateArray());

        Assert.Contains("hero", finding.GetProperty("Message").GetString());
        Assert.Equal("index.html", finding.GetProperty("File").GetString());
    }

    [Fact]
    public async Task AStyleBlockIsAnalysedWithTheRulesOfAnExternalSheet()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """
            <html lang="en">
            <head>
              <style>
                .card { color: red !important; }
                .missing { color: blue; }
              </style>
            </head>
            <body><div class="card hero"></div></body>
            </html>
            """);

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "html", "info", null, 100, 100));
        var findings = json.GetProperty("findings").EnumerateArray().ToList();

        var important = Assert.Single(findings, finding => finding.GetProperty("RuleId").GetString() == "CSS0001");
        Assert.Equal("index.html", important.GetProperty("File").GetString());
        Assert.Equal(4, important.GetProperty("Line").GetInt32());

        Assert.Single(findings, finding => finding.GetProperty("RuleId").GetString() == "CSS0009");
        Assert.Single(findings, finding => finding.GetProperty("RuleId").GetString() == "HTML0015");
    }

    [Fact]
    public void AnInlineStyleAttributeIsCheckedForTheCssSafetyRules()
    {
        Assert.Contains("CSS0001", HtmlRules("""<div style="color: red !important"></div>"""));
        Assert.Contains("CSS0007", HtmlRules("""<div style="background: url(javascript:alert(1))"></div>"""));
        Assert.Contains("CSS0008", HtmlRules("""<div style="width: expression(alert(1))"></div>"""));
        Assert.DoesNotContain("CSS0001", HtmlRules("""<div style="color: red"></div>"""));
    }

    [Fact]
    public async Task SelectorChecksAreSkippedWhenOneSideIsMissing()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """<div class="hero"></div>""");
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), ".missing { color: red; }");

        var htmlOnly = ResultAssert.Json(await service.AnalyzeAsync(root, "html", "info", null, 100, 100));
        Assert.Equal(0, htmlOnly.GetProperty("findingCount").GetInt32());
        Assert.Contains("no stylesheet is in scope", htmlOnly.GetProperty("note").GetString());

        var cssOnly = ResultAssert.Json(await service.AnalyzeAsync(root, "css", "info", null, 100, 100));
        Assert.Equal(0, cssOnly.GetProperty("findingCount").GetInt32());
        Assert.Contains("no HTML page is in scope", cssOnly.GetProperty("note").GetString());
    }

    [Fact]
    public void CssRules_ReadDeclarationsOrderAndSpecificity()
    {
        var rules = MarkupAnalyzer.CssRules("#x .a, div { color: red; margin: 0 !important; }");

        Assert.Equal(2, rules.Count);
        var byId = rules[0];
        Assert.Equal(2, byId.Declarations.Count);
        Assert.Equal("color", byId.Declarations[0].Property);
        Assert.Equal("red", byId.Declarations[0].Value);
        Assert.True(byId.Declarations[1].Important);
        Assert.Equal(10_100, byId.Specificity); // one id (10000) + one class (100)
        Assert.Equal(0, byId.Order);

        Assert.Equal(1, rules[1].Specificity); // one tag
        Assert.Equal(1, rules[1].Order);
    }

    [Fact]
    public async Task ADeclarationOverriddenOnEveryElementItMatchesIsReported()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """<div class="a b"></div>""");
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), """
            .a { color: red; }
            .b { color: blue; }
            """);

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "both", "info", "CSS0010", 100, 100));
        var finding = Assert.Single(json.GetProperty("findings").EnumerateArray());

        Assert.Contains("color: red", finding.GetProperty("Message").GetString());
        Assert.Contains(".a", finding.GetProperty("Snippet").GetString());
    }

    [Fact]
    public async Task AHigherSpecificityRuleMakesTheElementRuleDead()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), """<div class="a"></div>""");
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), """
            div { color: red; }
            .a { color: blue; }
            """);

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "both", "info", "CSS0010", 100, 100));
        var finding = Assert.Single(json.GetProperty("findings").EnumerateArray());
        Assert.Contains("color: red", finding.GetProperty("Message").GetString());
    }

    [Fact]
    public async Task ADeclarationThatWinsOnSomeElementIsNotReported()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new MarkupService(workspace);
        var root = workspace.RootDirectory;

        await File.WriteAllTextAsync(Path.Combine(root, "index.html"),
            """<div class="a"></div><div class="a b"></div>""");
        await File.WriteAllTextAsync(Path.Combine(root, "site.css"), """
            .a { color: red; }
            .b { color: blue; }
            """);

        var json = ResultAssert.Json(await service.AnalyzeAsync(root, "both", "info", "CSS0010", 100, 100));
        Assert.Equal(0, json.GetProperty("findingCount").GetInt32());
    }
}
