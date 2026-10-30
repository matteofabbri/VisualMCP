using Snitch.Languages.Razor;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Razor frontend of the metacode model: a page becomes a class that renders, the markup is kept
/// verbatim, and a construct the scanner cannot express is reported instead of disappearing.
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
    public void RazorFrontend_KeepsAnUnmodelledBlockAsOneOpaqueStatement()
    {
        const string page = """
            @switch (Model.Kind)
            {
                case 1:
                    <p>one</p>
                    break;
            }
            """;

        var document = Translate(page);

        Assert.Contains("RAW[razor] @switch", new PseudocodeRenderer().Render(document));
        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement &&
            diagnostic.Message.Contains("@switch"));
    }

    [Fact]
    public void RazorFrontend_ReportsABraceLessBodyInsteadOfDroppingIt()
    {
        const string page = """
            @if (Model.Any)
            <p>x</p>
            """;

        var document = Translate(page);

        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Message.Contains("@if"));
        Assert.Contains("RAW[razor] @if (Model.Any)", new PseudocodeRenderer().Render(document));
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
