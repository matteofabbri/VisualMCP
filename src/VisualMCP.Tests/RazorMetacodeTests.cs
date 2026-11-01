using Snitch.Languages.Razor;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Razor frontend of the metacode model: a page becomes a class that renders, every Razor control
/// construct becomes a node of the model, the markup between them is kept verbatim, and a construct the
/// frontend cannot read is reported instead of disappearing.
/// </summary>
public sealed class RazorMetacodeTests
{
    private const string Page = """
        @page "/products"
        @model ProductListViewModel
        @inject ICartService Cart
        @using MyApp.Data

        <h1>Products</h1>
        @if (Model.Any)
        {
            <ul>
                @foreach (var product in Model.Products)
                {
                    <li>@product.Name</li>
                }
            </ul>
        }
        else
        {
            <p>No products.</p>
        }

        @code {
            public void OnGet()
            {
                var count = 1;
            }
        }
        """;

    private static MetacodeDocument Translate(string text, string name = "Index.cshtml") =>
        new RazorFrontend().Translate(MetacodeSource.FromRazor(text, name), MetacodeOptions.Default);

    private static string Render(string text) => new PseudocodeRenderer().Render(Translate(text));

    private static MetacodeFunction RenderMethod(MetacodeDocument document) =>
        document.Modules
            .SelectMany(module => module.Declarations)
            .OfType<MetacodeFunction>()
            .Single(function => function.Name == "Render");

    [Fact]
    public void RazorFrontend_ModelsThePageAsAClassThatRenders()
    {
        var pseudocode = Render(Page);

        Assert.Contains("MODULE Index", pseudocode);
        Assert.Contains("PROPERTY Model : ProductListViewModel", pseudocode);
        Assert.Contains("PROPERTY Cart : ICartService", pseudocode);
        Assert.Contains("RAW[razor] @page \"/products\" // a Razor directive", pseudocode);
        Assert.Contains("METHOD Render", pseudocode);
        Assert.Contains("RAW[razor] <h1>Products</h1>", pseudocode);
        Assert.Contains("IF Model.Any THEN", pseudocode);
        Assert.Contains("FOREACH product IN Model.Products DO", pseudocode);
        Assert.Contains("RAW[razor] <li>@product.Name</li>", pseudocode);
        Assert.Contains("ELSE", pseudocode);
        Assert.Contains("RAW[razor] <p>No products.</p>", pseudocode);
        Assert.Contains("METHOD OnGet()", pseudocode);
        Assert.Contains("DECLARE count = 1", pseudocode);
    }

    [Fact]
    public void RazorFrontend_ModelsAnElseIfChain()
    {
        const string page = """
            @if (a)
            {
                <p>a</p>
            }
            else if (b)
            {
                <p>b</p>
            }
            else
            {
                <p>c</p>
            }
            """;

        var pseudocode = Render(page);

        Assert.Contains("IF a THEN", pseudocode);
        Assert.Contains("ELSE IF b THEN", pseudocode);
        Assert.Contains("ELSE", pseudocode);
        Assert.Contains("RAW[razor] <p>c</p>", pseudocode);
    }

    [Fact]
    public void RazorFrontend_ModelsASwitchWithItsCasesAndBreak()
    {
        const string page = """
            @switch (Model.Kind)
            {
                case 1:
                case 2:
                    <p>one or two</p>
                    break;
                default:
                    <p>other</p>
                    break;
            }
            """;

        var document = Translate(page);
        var pseudocode = new PseudocodeRenderer().Render(document);

        var @switch = Assert.IsType<MetacodeSwitch>(RenderMethod(document).Body!.Statements[0]);
        Assert.Equal(2, @switch.Cases.Count);
        Assert.False(@switch.Cases[0].IsDefault);
        Assert.Equal(2, @switch.Cases[0].Labels!.Count);
        Assert.True(@switch.Cases[1].IsDefault);
        Assert.Contains(@switch.Cases[0].Body.Statements, statement => statement is MetacodeBreak);
        Assert.Contains(@switch.Cases[1].Body.Statements, statement => statement is MetacodeBreak);

        Assert.Contains("SWITCH Model.Kind", pseudocode);
        Assert.Contains("CASE 1, 2", pseudocode);
        Assert.Contains("DEFAULT", pseudocode);
        Assert.Contains("BREAK", pseudocode);
        Assert.DoesNotContain("RAW[razor] @switch", pseudocode);
        Assert.DoesNotContain(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
    }

    [Fact]
    public void RazorFrontend_ModelsTryCatchFinally()
    {
        const string page = """
            @try
            {
                <p>try</p>
            }
            catch (InvalidOperationException ex) when (ex.Message.Length > 0)
            {
                <p>catch</p>
            }
            finally
            {
                <p>finally</p>
            }
            """;

        var document = Translate(page);
        var pseudocode = new PseudocodeRenderer().Render(document);

        var @try = Assert.IsType<MetacodeTry>(RenderMethod(document).Body!.Statements[0]);
        var @catch = Assert.Single(@try.Catches);
        Assert.Equal("InvalidOperationException", @catch.ExceptionType!.Render());
        Assert.Equal("ex", @catch.VariableName);
        Assert.NotNull(@catch.Filter);
        Assert.NotNull(@try.Finally);

        Assert.Contains("TRY", pseudocode);
        Assert.Contains("CATCH (InvalidOperationException ex) WHEN ex.Message.Length > 0", pseudocode);
        Assert.Contains("FINALLY", pseudocode);
        Assert.Contains("END TRY", pseudocode);
        Assert.Contains("RAW[razor] <p>finally</p>", pseudocode);
        Assert.DoesNotContain(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
    }

    [Fact]
    public void RazorFrontend_ModelsDoWhile()
    {
        const string page = """
            @do
            {
                <p>body</p>
            }
            while (Model.More);
            """;

        var document = Translate(page);
        var pseudocode = new PseudocodeRenderer().Render(document);

        Assert.Contains("REPEAT", pseudocode);
        Assert.Contains("UNTIL Model.More", pseudocode);
        Assert.Contains("RAW[razor] <p>body</p>", pseudocode);
        Assert.DoesNotContain(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
    }

    [Fact]
    public void RazorFrontend_ModelsABraceLessBody()
    {
        const string page = """
            @if (Model.Any)
            <p>x</p>
            """;

        var document = Translate(page);
        var pseudocode = new PseudocodeRenderer().Render(document);

        var @if = Assert.IsType<MetacodeIf>(RenderMethod(document).Body!.Statements[0]);
        var markup = Assert.IsType<MetacodeOpaqueStatement>(Assert.Single(@if.Then.Statements));
        Assert.Equal("<p>x</p>", markup.Text);

        Assert.Contains("IF Model.Any THEN", pseudocode);
        Assert.Contains("RAW[razor] <p>x</p>", pseudocode);
        Assert.DoesNotContain(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
    }

    [Fact]
    public void RazorFrontend_ModelsABraceLessBodyWithItsElseBranch()
    {
        const string page = """
            @if (a)
            <p>a</p>
            else
            <p>b</p>
            """;

        var document = Translate(page);

        var @if = Assert.IsType<MetacodeIf>(RenderMethod(document).Body!.Statements[0]);
        Assert.NotNull(@if.Else);
        Assert.Equal("<p>a</p>", Assert.IsType<MetacodeOpaqueStatement>(Assert.Single(@if.Then.Statements)).Text);
        Assert.Equal("<p>b</p>", Assert.IsType<MetacodeOpaqueStatement>(Assert.Single(@if.Else!.Statements)).Text);
    }

    [Fact]
    public void RazorFrontend_LowersATopLevelCodeBlockInPlace()
    {
        const string page = """
            @{ var count = items.Count; }
            <p>@count</p>
            """;

        var document = Translate(page);
        var statements = RenderMethod(document).Body!.Statements;
        var pseudocode = new PseudocodeRenderer().Render(document);

        var declared = Assert.IsType<MetacodeLocalVariable>(statements[0]);
        Assert.Equal("count", declared.Name);
        Assert.Contains("RAW[razor] <p>@count</p>", pseudocode);
        Assert.DoesNotContain("var count", pseudocode);
    }

    [Fact]
    public void RazorFrontend_ModelsASectionAsItsOwnMethod()
    {
        const string page = """
            @section Scripts {
                <script src="app.js"></script>
            }
            <h1>hi</h1>
            """;

        var document = Translate(page);
        var pseudocode = new PseudocodeRenderer().Render(document);

        var section = document.Modules
            .SelectMany(module => module.Declarations)
            .OfType<MetacodeFunction>()
            .Single(function => function.Name == "Section_Scripts");

        Assert.Contains("METHOD Section_Scripts", pseudocode);
        Assert.Contains("RAW[razor] <script src=\"app.js\"></script>", pseudocode);

        // The section body belongs to the section, not to the page body.
        Assert.Single(section.Body!.Statements);
        Assert.Equal("<h1>hi</h1>", Assert.IsType<MetacodeOpaqueStatement>(
            Assert.Single(RenderMethod(document).Body!.Statements)).Text);
    }

    [Fact]
    public void RazorFrontend_ModelsAControlConstructInsideASwitchCase()
    {
        const string page = """
            @switch (Model.Kind)
            {
                case 1:
                    @if (Model.Any)
                    {
                        <p>a</p>
                    }
                    break;
            }
            """;

        var document = Translate(page);
        var @switch = Assert.IsType<MetacodeSwitch>(RenderMethod(document).Body!.Statements[0]);
        var @case = Assert.Single(@switch.Cases);

        Assert.Contains(@case.Body.Statements, statement => statement is MetacodeIf);
        Assert.Contains(@case.Body.Statements, statement => statement is MetacodeBreak);
    }

    [Fact]
    public void RazorFrontend_ReportsThePageLineOfACodeMember()
    {
        const string page = """
            <h1>x</h1>
            @code {
                public string Title => "x";
            }
            """;

        var member = Translate(page).Modules
            .SelectMany(module => module.Declarations)
            .OfType<MetacodeProperty>()
            .Single(property => property.Name == "Title");

        Assert.Equal(3, member.Origin.StartLine);
    }

    [Fact]
    public void RazorFrontend_DoesNotNeedAnElseBranch()
    {
        const string page = """
            @if (Model.Any)
            {
                <p>yes</p>
            }
            """;

        var pseudocode = Render(page);

        Assert.Contains("IF Model.Any THEN", pseudocode);
        Assert.DoesNotContain("ELSE", pseudocode);
    }

    [Fact]
    public void RazorSourceFromFile_PicksTheRazorFrontendByExtension()
    {
        var path = Path.Combine(Path.GetTempPath(), "vmcp-razor-frontend-" + Guid.NewGuid().ToString("N") + ".cshtml");
        File.WriteAllText(path, "@page\n<p>x</p>\n");

        try
        {
            var source = MetacodeSource.FromFile(path);
            var document = MetacodeTranslator.Default.Translate(source);

            Assert.Equal(MetacodeLanguage.Razor, source.Language);
            Assert.Equal(MetacodeLanguage.Razor, document.Language);
            Assert.DoesNotContain(document.Diagnostics, diagnostic =>
                diagnostic.Code == MetacodeDiagnosticCodes.NoFrontend);
            Assert.Equal("razor", PseudocodeRenderer.LanguageTag(document.Language));
            Assert.Equal(MetacodeLanguage.Razor, PseudocodeRenderer.LanguageFromTag("razor"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RazorFrontend_DeclinesANonRazorSource()
    {
        var frontend = new RazorFrontend();

        Assert.False(frontend.CanHandle(MetacodeSource.FromCSharp("class C { }", "C.cs")));
        Assert.True(frontend.CanHandle(MetacodeSource.FromRazor("<p>x</p>", "Index.cshtml")));
    }
}
