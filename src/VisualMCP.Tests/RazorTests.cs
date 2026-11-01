using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.Razor;
using Snitch.Workspace;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Razor layer: the scanner that separates markup from C#, the rule catalogue and the three
/// services, over real files in a temp folder. It also pins the false positives that matter — a
/// Blazor @onclick, an e-mail address and the form tag helper must not be reported.
/// </summary>
public sealed class RazorTests
{
    private const string ViewPath = "Views/Home/Index.cshtml";

    private static RazorDocument Scan(string text, string file = ViewPath) => RazorScanner.Scan(file, text);

    private static List<RazorFinding> Analyze(string text, string file = ViewPath)
    {
        var document = RazorScanner.Scan(file, text);
        document.Kind = RazorPageClassifier.Classify(document, file);
        return RazorRules.Run(document);
    }

    private static HashSet<string> RuleIds(string text, string file = ViewPath) =>
        Analyze(text, file).Select(finding => finding.RuleId).ToHashSet(StringComparer.Ordinal);

    // ── Scanner ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scanner_ReadsDirectivesSectionsAndInjections()
    {
        const string source = """
            @page "/products/{id:int}"
            @model Shop.Web.Pages.ProductModel
            @layout _Layout
            @inject IProductService Products
            @using Shop.Web.Models

            @section Scripts {
                <script src="~/js/product.js"></script>
            }

            <h1>@Model.Title</h1>
            """;

        var document = Scan(source, "Pages/Product.cshtml");

        Assert.Equal("/products/{id:int}", document.Route);
        Assert.Equal("Shop.Web.Pages.ProductModel", document.ModelType);
        Assert.Equal("_Layout", document.LayoutDirective);
        Assert.Contains("Shop.Web.Models", document.Usings);
        Assert.Contains("Scripts", document.Sections);
        Assert.Contains(document.Injections,
            injection => injection.ServiceType == "IProductService" && injection.PropertyName == "Products");
        Assert.Contains(document.Expressions, expression => expression.Text == "Model.Title");
        Assert.Empty(document.SyntaxIssues);
    }

    [Fact]
    public void Scanner_HandlesBracesInsideStringsCommentsAndInterpolation()
    {
        const string source = """
            @{
                var a = "}";
                var b = @"}";
                // }
                /* } */
                var c = $"{a}}";
            }
            <p>@Model.Name</p>
            """;

        var document = Scan(source);

        Assert.Empty(document.SyntaxIssues);
        var block = Assert.Single(document.CodeBlocks);
        Assert.Contains("var c", block.Text);
        Assert.Single(document.Expressions);
        Assert.Equal("Model.Name", document.Expressions[0].Text);
        Assert.Single(document.Elements);
    }

    [Fact]
    public void Scanner_ReportsAnUnclosedCodeBlock()
    {
        var document = Scan("@{\n    var x = 1;\n");

        Assert.Contains(document.SyntaxIssues, issue => issue.RuleId == "RZ2015");
    }

    [Fact]
    public void Scanner_TracksElementNestingAndVoidElements()
    {
        var document = Scan("<div>\n  <input type=\"text\">\n  <span>@Model.Name</span>\n</div>");

        Assert.Empty(document.SyntaxIssues);
        var div = Assert.Single(document.Elements);
        Assert.Equal("div", div.Name);
        Assert.Equal(2, div.Children.Count);
        Assert.True(div.Children[0].IsVoid);
        Assert.Equal("span", div.Children[1].Name);
    }

    [Fact]
    public void Scanner_ReportsAnUnclosedDiv()
    {
        var document = Scan("<div><span>x</span>");

        Assert.Contains(document.SyntaxIssues,
            issue => issue.RuleId == "RZ2001" && issue.Message.Contains("div"));
    }

    [Fact]
    public void Scanner_RecordsMarkupInsideAControlBlockAndItsLoopVariable()
    {
        const string source = """
            @foreach (var item in Model.Items) {
                <li>@item.Name</li>
            }
            """;

        var document = Scan(source);

        Assert.Contains("item", document.LoopVariables);
        Assert.Single(document.Elements);
        Assert.Contains(document.Expressions, expression => expression.Text == "item.Name");
        Assert.DoesNotContain(document.SyntaxIssues, issue => issue.RuleId == "RZ2015");
    }

    [Fact]
    public void Scanner_DoesNotTreatAUsingStatementAsAnImport()
    {
        const string source = """
            @using (Html.BeginForm("Save")) {
                <input name="x" />
            }
            """;

        var document = Scan(source);

        Assert.Empty(document.Usings);
        Assert.Single(document.Elements);
        Assert.Contains(document.CodeBlocks, block => block.Kind == "control");
    }

    [Fact]
    public void Scanner_IgnoresRazorComments()
    {
        var document = Scan("@* <div> *@\n<p>x</p>");

        Assert.Empty(document.SyntaxIssues);
        Assert.Single(document.Elements);
    }

    [Fact]
    public void Scanner_ParsesARealisticLayoutWithoutMarkupIssues()
    {
        const string layout = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>@ViewData["Title"] - Shop</title>
                <link rel="stylesheet" href="~/lib/bootstrap/dist/css/bootstrap.min.css" asp-append-version="true" />
            </head>
            <body>
                <header>
                    <nav class="navbar navbar-expand-sm navbar-light bg-white border-bottom">
                        <div class="container">
                            <a class="navbar-brand" asp-area="" asp-page="/Index">Shop</a>
                            <button class="navbar-toggler" type="button" data-bs-toggle="collapse">
                                <span class="navbar-toggler-icon"></span>
                            </button>
                        </div>
                    </nav>
                </header>
                <div class="container">
                    <main role="main" class="pb-3">
                        @RenderBody()
                    </main>
                </div>
                @await RenderSectionAsync("Scripts", required: false)
            </body>
            </html>
            """;

        var document = Scan(layout, "Views/Shared/_Layout.cshtml");
        var findings = RazorRules.Run(document);

        Assert.Empty(document.SyntaxIssues);
        Assert.Equal(RazorPageKind.Layout,
            RazorPageClassifier.Classify(document, "Views/Shared/_Layout.cshtml"));
        Assert.DoesNotContain(findings,
            finding => finding.RuleId is "RZ2001" or "RZ2002" or "RZ2015" or "RZ2006" or "RZ2007" or "RZ2008" or "RZ2009");
    }

    [Fact]
    public void AFormTagHelperForm_IsNotReportedAsCsrfOrMissingName()
    {
        const string page = """
            @page
            @model IndexModel
            <form method="post" asp-page-handler="Delete">
                <input asp-for="Id" />
                <button type="submit">Delete</button>
            </form>
            """;

        var findings = Analyze(page, "Pages/Index.cshtml");

        Assert.DoesNotContain(findings, finding => finding.RuleId is "RZ1005" or "RZ2006" or "RZ2007");
    }

    [Fact]
    public void AntiForgery_LooksInsideThePartialTheFormRenders()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-razor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Views", "Home"));
        Directory.CreateDirectory(Path.Combine(root, "Views", "Shared"));

        var page = Path.Combine(root, "Views", "Home", "Edit.cshtml");
        var tokenPartial = Path.Combine(root, "Views", "Shared", "_Token.cshtml");
        var cleanPartial = Path.Combine(root, "Views", "Shared", "_NoToken.cshtml");
        File.WriteAllText(tokenPartial, "@Html.AntiForgeryToken()");
        File.WriteAllText(cleanPartial, "<p>nothing here</p>");
        File.WriteAllText(page, "<form method=\"post\"></form>");

        try
        {
            var collection = new RazorCollection { Root = root };
            collection.Files.Add(new RazorFileEntry(page, "Views/Home/Edit.cshtml", null));
            collection.Files.Add(new RazorFileEntry(tokenPartial, "Views/Shared/_Token.cshtml", null));
            collection.Files.Add(new RazorFileEntry(cleanPartial, "Views/Shared/_NoToken.cshtml", null));

            var resolver = RazorAnalysis.CreatePartialResolver(collection)
                .For(Path.GetDirectoryName(page));

            var tagHelper = Scan("""
                <form method="post" action="/save">
                    <partial name="_Token" />
                    <button type="submit">Save</button>
                </form>
                """, "Views/Home/Edit.cshtml");
            Assert.DoesNotContain(RazorRules.Run(tagHelper, resolver), finding => finding.RuleId == "RZ1005");

            var htmlHelper = Scan("""
                <form method="post" action="/save">
                    @await Html.PartialAsync("_Token")
                </form>
                """, "Views/Home/Edit.cshtml");
            Assert.DoesNotContain(RazorRules.Run(htmlHelper, resolver), finding => finding.RuleId == "RZ1005");

            var withoutToken = Scan("""
                <form method="post" action="/save">
                    <partial name="_NoToken" />
                </form>
                """, "Views/Home/Edit.cshtml");
            Assert.Contains(RazorRules.Run(withoutToken, resolver), finding => finding.RuleId == "RZ1005");

            // Without a resolver the rule stays per file, which is the behaviour the limit named.
            Assert.Contains(RazorRules.Run(htmlHelper), finding => finding.RuleId == "RZ1005");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DuplicateIds_CompareThePageWithItsLayoutAndThePartialsItRenders()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-razor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Views", "Home"));
        Directory.CreateDirectory(Path.Combine(root, "Views", "Shared"));

        var page = Path.Combine(root, "Views", "Home", "Edit.cshtml");
        var layout = Path.Combine(root, "Views", "Shared", "_Layout.cshtml");
        var widget = Path.Combine(root, "Views", "Shared", "_Widget.cshtml");
        var other = Path.Combine(root, "Views", "Shared", "_Other.cshtml");
        var duplicated = Path.Combine(root, "Views", "Shared", "_Duplicated.cshtml");

        // A layout selected in a code block, the MVC spelling, is followed too.
        File.WriteAllText(page, """
            @{ Layout = "_Layout"; }
            <div id="main">page</div>
            <partial name="_Widget" />
            <partial name="_Other" />
            <partial name="_Duplicated" />
            """);
        File.WriteAllText(layout, "<html><body><div id=\"main\">@RenderBody()</div></body></html>");
        File.WriteAllText(widget, "<section id=\"widget\"></section>");
        File.WriteAllText(other, "<aside id=\"widget\"></aside>");
        File.WriteAllText(duplicated, "<b id=\"twice\"></b><i id=\"twice\"></i>");

        try
        {
            var collection = new RazorCollection { Root = root };
            foreach (var path in new[] { page, layout, widget, other, duplicated })
                collection.Files.Add(new RazorFileEntry(
                    path, Path.GetRelativePath(root, path).Replace('\\', '/'), null));

            var resolver = RazorAnalysis.CreatePartialResolver(collection).For(Path.GetDirectoryName(page));
            var document = Scan(File.ReadAllText(page), "Views/Home/Edit.cshtml");
            document.Kind = RazorPageClassifier.Classify(document, "Views/Home/Edit.cshtml");
            var duplicates = RazorRules.Run(document, resolver)
                .Where(finding => finding.RuleId == "RZ2003").ToList();

            // The page and the layout both declare "main": reported on the layout, naming the page.
            var layoutCollision = Assert.Single(duplicates, finding => finding.Snippet.Contains("id=\"main\""));
            Assert.Equal(layout, layoutCollision.FilePath);
            Assert.Contains("Edit.cshtml", layoutCollision.Message);

            // Two partials declare "widget": reported on the second, naming the first.
            var partialCollision = Assert.Single(duplicates, finding => finding.Snippet.Contains("id=\"widget\""));
            Assert.Equal(other, partialCollision.FilePath);
            Assert.Contains("_Widget.cshtml", partialCollision.Message);

            // A partial colliding with itself is that file's problem, not one per page that renders it.
            Assert.DoesNotContain(duplicates, finding => finding.Snippet.Contains("id=\"twice\""));
            var onItsOwn = Scan(File.ReadAllText(duplicated), "Views/Shared/_Duplicated.cshtml");
            Assert.Contains(RazorRules.Run(onItsOwn), finding => finding.RuleId == "RZ2003");

            // Without a resolver the rule is per file: the page's own ids are all distinct, so nothing.
            Assert.DoesNotContain(RazorRules.Run(document), finding => finding.RuleId == "RZ2003");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Security rules ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("@Html.Raw(Model.Html)", "RZ1001")]
    [InlineData("@(new HtmlString(Model.Html))", "RZ1001")]
    [InlineData("<script>var x = @Model.Json;</script>", "RZ1002")]
    [InlineData("<button onclick=\"@Model.Handler\">go</button>", "RZ1003")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "RZ1004")]
    [InlineData("<form method=\"post\"><input name=\"x\" /></form>", "RZ1005")]
    [InlineData("<form><input type=\"password\" name=\"p\" /></form>", "RZ1006")]
    [InlineData("<a href=\"https://example.com\" target=\"_blank\">x</a>", "RZ1007")]
    [InlineData("<input type=\"password\" name=\"p\" value=\"hunter2\" />", "RZ1008")]
    [InlineData("<script src=\"http://cdn.example.com/x.js\"></script>", "RZ1009")]
    [InlineData("<iframe src=\"@Model.Url\"></iframe>", "RZ1010")]
    [InlineData("<script>var a = 1;</script>", "RZ1011")]
    [InlineData("<div title=\"@Html.Raw(Model.T)\"></div>", "RZ1001")]
    public void SecurityRules_FireOnTheVulnerableShape(string source, string ruleId)
    {
        Assert.Contains(ruleId, RuleIds(source));
    }

    [Theory]
    [InlineData("@Html.Raw(\"<b>static</b>\")", "RZ1001")]
    [InlineData("@Html.Raw(Html.Encode(Model.Html))", "RZ1001")]
    [InlineData("<script>var x = @Json.Serialize(Model.Json);</script>", "RZ1002")]
    [InlineData("<form method=\"post\">@Html.AntiForgeryToken()</form>", "RZ1005")]
    [InlineData("<form method=\"post\" asp-action=\"Save\"></form>", "RZ1005")]
    [InlineData("<form><input type=\"password\" name=\"p\" value=\"@Model.P\" /></form>", "RZ1008")]
    [InlineData("<a href=\"https://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">x</a>", "RZ1007")]
    [InlineData("<script src=\"https://cdn.example.com/x.js\"></script>", "RZ1009")]
    public void SecurityRules_DoNotFireOnTheSafeShape(string source, string ruleId)
    {
        Assert.DoesNotContain(ruleId, RuleIds(source));
    }

    [Fact]
    public void SecurityFinding_CarriesCweSeverityAndRemediation()
    {
        var finding = Assert.Single(Analyze("@Html.Raw(Model.Html)"), item => item.RuleId == "RZ1001");

        Assert.Equal("Security", finding.Category);
        Assert.Equal("High", finding.Severity);
        Assert.Equal("CWE-79", finding.Cwe);
        Assert.False(string.IsNullOrWhiteSpace(finding.Suggestion));
        Assert.Equal(1, finding.Line);
        Assert.Contains("Html.Raw", finding.Snippet);
    }

    // ── Markup, accessibility and maintainability ──────────────────────────────

    [Theory]
    [InlineData("<div>", "RZ2001")]
    [InlineData("</span>", "RZ2002")]
    [InlineData("<div id=\"a\"></div><p id=\"a\"></p>", "RZ2003")]
    [InlineData("<img src=\"x\" />", "RZ2004")]
    [InlineData("<label>Name</label>", "RZ2005")]
    [InlineData("<form><button>x</button></form>", "RZ2006")]
    [InlineData("<form><input type=\"text\" /></form>", "RZ2007")]
    [InlineData("<html><body>x</body></html>", "RZ2008")]
    [InlineData("<html><body>x</body></html>", "RZ2009")]
    [InlineData("<p>@ViewBag.Title</p>", "RZ2010")]
    [InlineData("@Html.Partial(\"_X\")", "RZ2011")]
    [InlineData("<form><div><form></form></div></form>", "RZ2013")]
    [InlineData("<div class=\"a\" class=\"b\"></div>", "RZ2014")]
    [InlineData("<form method=\"get\"></form>", "RZ2016")]
    [InlineData("<center>x</center>", "RZ2017")]
    public void ProblemRules_FireOnTheBrokenShape(string source, string ruleId)
    {
        Assert.Contains(ruleId, RuleIds(source));
    }

    [Theory]
    [InlineData("<div class=\"a\"></div>", "RZ2014")]
    [InlineData("<img src=\"x\" alt=\"A product\" />", "RZ2004")]
    [InlineData("<label for=\"name\">Name</label><input id=\"name\" name=\"name\" />", "RZ2005")]
    [InlineData("<form><input asp-for=\"Name\" /></form>", "RZ2007")]
    [InlineData("<form method=\"post\" asp-action=\"Save\"><button asp-action=\"Save\">Go</button></form>", "RZ2006")]
    [InlineData("<html lang=\"en\"><head><meta name=\"viewport\" content=\"width=device-width\" /></head><body>x</body></html>", "RZ2008")]
    public void ProblemRules_DoNotFireOnTheCorrectShape(string source, string ruleId)
    {
        Assert.DoesNotContain(ruleId, RuleIds(source));
    }

    [Fact]
    public void ProblemRules_FlagALargeCodeBlock()
    {
        var body = string.Join("\n", Enumerable.Range(0, 30).Select(index => $"    var v{index} = {index};"));
        var source = "@{\n" + body + "\n}";

        Assert.Contains("RZ2012", RuleIds(source));
    }

    [Fact]
    public void ProblemRules_DoNotReportAnEmailAddressOrABlazorHandler()
    {
        Assert.Empty(Analyze("<p>write to support@contoso.com</p>"));

        var blazor = Analyze("<button @onclick=\"Increment\">@count</button>", "Components/Counter.razor");
        Assert.DoesNotContain(blazor, finding => finding.RuleId is "RZ1005" or "RZ1003" or "RZ1011");
    }

    // ── Catalogue ──────────────────────────────────────────────────────────────

    [Fact]
    public void Catalogue_IsConsistentAndExplainsEveryRule()
    {
        var ids = RazorRuleCatalog.Rules.Select(rule => rule.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(ids.Count, 25, 40);
        Assert.All(RazorRuleCatalog.Rules, rule =>
        {
            Assert.Contains(rule.Severity, RazorRuleCatalog.Severities);
            Assert.False(string.IsNullOrWhiteSpace(rule.Suggestion));
            Assert.DoesNotContain("TODO", rule.Suggestion, StringComparison.OrdinalIgnoreCase);

            if (rule.Category == "Security")
                Assert.StartsWith("CWE-", rule.Cwe);
        });
    }

    [Fact]
    public void Catalogue_RanksSeverityFromInfoToCritical()
    {
        Assert.True(RazorRuleCatalog.Rank("Critical") > RazorRuleCatalog.Rank("High"));
        Assert.True(RazorRuleCatalog.Rank("High") > RazorRuleCatalog.Rank("Medium"));
        Assert.True(RazorRuleCatalog.Rank("Medium") > RazorRuleCatalog.Rank("Low"));
        Assert.True(RazorRuleCatalog.Rank("Low") > RazorRuleCatalog.Rank("Info"));
        Assert.Equal(0, RazorRuleCatalog.Rank("bogus"));
    }

    // ── Services ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RazorPages_ClassifiesTheFilesAndReadsTheirModel()
    {
        using var folder = new TempFolder();
        folder.Write("Pages/Index.cshtml", "@page \"/\"\n@model IndexModel\n<h1>@Model.Title</h1>");
        folder.Write("Views/Home/Index.cshtml", "@model HomeViewModel\n<h1>@Model.Title</h1>");
        folder.Write("Views/Shared/_Layout.cshtml",
            "<html lang=\"en\"><head><meta name=\"viewport\" content=\"width=device-width\" /></head><body>@RenderBody()</body></html>");
        folder.Write("Views/Shared/_Part.cshtml", "<p>part</p>");
        folder.Write("Components/Counter.razor", "<button @onclick=\"Increment\">@count</button>");

        var service = new RazorPagesService(new NullWorkspace());
        var result = Json(await service.ListAsync(null, folder.Path, 100));

        Assert.Equal(5, result.GetProperty("pageCount").GetInt32());
        Assert.Equal(1, result.GetProperty("byKind").GetProperty("page").GetInt32());
        Assert.Equal(1, result.GetProperty("byKind").GetProperty("view").GetInt32());
        Assert.Equal(1, result.GetProperty("byKind").GetProperty("layout").GetInt32());
        Assert.Equal(1, result.GetProperty("byKind").GetProperty("partial").GetInt32());
        Assert.Equal(1, result.GetProperty("byKind").GetProperty("component").GetInt32());

        var pages = result.GetProperty("pages").EnumerateArray().ToList();
        var page = pages.Single(item => item.GetProperty("relativePath").GetString()!.EndsWith("Pages/Index.cshtml"));
        Assert.Equal("/", page.GetProperty("route").GetString());
        Assert.Equal("IndexModel", page.GetProperty("model").GetString());

        var view = pages.Single(item => item.GetProperty("relativePath").GetString()!.EndsWith("Views/Home/Index.cshtml"));
        Assert.Equal("view", view.GetProperty("kind").GetString());
        Assert.Equal("HomeViewModel", view.GetProperty("model").GetString());
    }

    [Fact]
    public async Task AnalyzeRazorPages_ReturnsProblemsButNoSecurityFindings()
    {
        using var folder = new TempFolder();
        folder.Write(ViewPath,
            "<form method=\"post\"><input name=\"x\" /></form><p>@ViewBag.Title</p><img src=\"x\">");

        var service = new AnalyzeRazorPagesService(new NullWorkspace());
        var result = Json(await service.AnalyzeAsync(null, folder.Path, null, null, "Low", 100));

        Assert.True(result.GetProperty("totalFindings").GetInt32() >= 2);
        Assert.All(result.GetProperty("findings").EnumerateArray(),
            finding => Assert.NotEqual("Security", finding.GetProperty("category").GetString()));
        Assert.Contains(result.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("ruleId").GetString() == "RZ2004");
    }

    [Fact]
    public async Task AnalyzeRazorPages_AppliesTheSeverityAndRuleFilters()
    {
        using var folder = new TempFolder();
        folder.Write(ViewPath, "<img src=\"x\" /><center>old</center>");

        var service = new AnalyzeRazorPagesService(new NullWorkspace());

        var highOnly = Json(await service.AnalyzeAsync(null, folder.Path, null, null, "High", 100));
        Assert.Equal(0, highOnly.GetProperty("totalFindings").GetInt32());

        var oneRule = Json(await service.AnalyzeAsync(null, folder.Path, "Accessibility", "RZ2004", "Low", 100));
        Assert.Equal(1, oneRule.GetProperty("totalFindings").GetInt32());
        Assert.Equal("RZ2004", oneRule.GetProperty("findings")[0].GetProperty("ruleId").GetString());
    }

    [Fact]
    public async Task FindRazorVulnerabilities_ReturnsOnlyCweTaggedSecurityFindings()
    {
        using var folder = new TempFolder();
        folder.Write(ViewPath, "@Html.Raw(Model.Html)\n<form method=\"post\"><input name=\"x\" /></form>");

        var service = new FindRazorVulnerabilitiesService(new NullWorkspace());
        var result = Json(await service.FindAsync(null, folder.Path, "Low", null, 100));

        var findings = result.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding =>
        {
            Assert.Equal("Security", finding.GetProperty("category").GetString());
            Assert.StartsWith("CWE-", finding.GetProperty("cwe").GetString());
        });
        Assert.Equal(1, result.GetProperty("byCwe").GetProperty("CWE-79").GetInt32());
        Assert.Equal(1, result.GetProperty("byCwe").GetProperty("CWE-352").GetInt32());
    }

    [Fact]
    public async Task FindRazorVulnerabilities_FiltersByCweAndSeverity()
    {
        using var folder = new TempFolder();
        folder.Write(ViewPath,
            "@Html.Raw(Model.Html)\n<form method=\"post\"><input name=\"x\" /></form>");

        var service = new FindRazorVulnerabilitiesService(new NullWorkspace());

        var xss = Json(await service.FindAsync(null, folder.Path, "Low", "79", 100));
        Assert.Equal(1, xss.GetProperty("totalFindings").GetInt32());
        Assert.Equal("RZ1001", xss.GetProperty("findings")[0].GetProperty("ruleId").GetString());

        var high = Json(await service.FindAsync(null, folder.Path, "High", null, 100));
        Assert.Equal(1, high.GetProperty("totalFindings").GetInt32());
    }

    [Fact]
    public async Task RazorServices_RejectAnUnknownSeverity()
    {
        var analyze = Json(await new AnalyzeRazorPagesService(new NullWorkspace())
            .AnalyzeAsync(null, null, null, null, "Severe", 10));
        Assert.Contains("Unknown severity", analyze.GetProperty("error").GetString());

        var vulnerabilities = Json(await new FindRazorVulnerabilitiesService(new NullWorkspace())
            .FindAsync(null, null, "Severe", null, 10));
        Assert.Contains("Unknown severity", vulnerabilities.GetProperty("error").GetString());
    }

    [Fact]
    public void AddSnitch_RegistersTheThreeRazorServices()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Snitch.SnitchServiceCollectionExtensions.AddSnitch(services);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IRazorPagesService) &&
            descriptor.ImplementationType == typeof(RazorPagesService));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAnalyzeRazorPagesService) &&
            descriptor.ImplementationType == typeof(AnalyzeRazorPagesService));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IFindRazorVulnerabilitiesService) &&
            descriptor.ImplementationType == typeof(FindRazorVulnerabilitiesService));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A folder under %TEMP% that the services read exactly as they read a repository.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vmcp-razor-tests-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Write(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(
                Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>No solution: every service test drives the path-based branch, which needs no Roslyn.</summary>
    private sealed class NullWorkspace : IRoslynWorkspace
    {
        public Solution? CurrentSolution => null;
        public string? LoadedSolutionPath => null;

        public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) =>
            Task.FromResult<Solution?>(null);

        public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? DiscoverSolutionPath() => null;

        public bool TryUpdateDocument(DocumentId documentId, SourceText text) => false;

        public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text) => null;

        public bool TryRemoveDocument(DocumentId documentId) => false;

        public void Dispose() { }
    }
}
