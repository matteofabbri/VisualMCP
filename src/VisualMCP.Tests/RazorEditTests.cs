using Microsoft.Extensions.DependencyInjection;
using Snitch.Languages.Razor;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Razor write side: <see cref="RazorEditService"/> over real files in a temp folder, with the
/// real scanner and rule set behind it, so a refusal is the gate the tool actually ships.
/// </summary>
public sealed class RazorEditTests
{
    // ── set-attribute / remove-attribute ───────────────────────────────────────

    [Fact]
    public async Task SetAttribute_AddsTheAttributeAndResolvesTheRule()
    {
        using var file = new TempRazor("Index.cshtml", """
            @page
            <a href="https://example.com" target="_blank">site</a>
            """);

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            AttributeName = "rel",
            AttributeValue = "noopener noreferrer",
            DryRun = false,
        });

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.Contains("rel=\"noopener noreferrer\"", file.Read());
        Assert.Contains(json.GetProperty("findings").GetProperty("resolved").EnumerateArray(),
            entry => entry.GetProperty("ruleId").GetString() == "RZ1007");
    }

    [Fact]
    public async Task SetAttribute_DryRunChangesNothing()
    {
        using var file = new TempRazor("Index.cshtml", "<img src=\"/a.png\" />\n");
        var before = file.Read();

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "img",
            AttributeName = "alt",
            AttributeValue = "A picture",
        });

        var json = ResultAssert.Json(result);
        Assert.False(json.GetProperty("applied").GetBoolean());
        Assert.True(json.GetProperty("changed").GetBoolean());
        Assert.Contains("+ <img src=\"/a.png\" alt=\"A picture\" />", json.GetProperty("diff").GetString());
        Assert.Equal(before, file.Read());
    }

    [Fact]
    public async Task SetAttribute_ReplacesAnExistingValue()
    {
        using var file = new TempRazor("Index.cshtml", "<a href=\"/a\" rel=\"nofollow\">x</a>\n");

        await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            AttributeName = "rel",
            AttributeValue = "noopener",
            DryRun = false,
        });

        Assert.Contains("rel=\"noopener\"", file.Read());
        Assert.DoesNotContain("nofollow", file.Read());
    }

    [Fact]
    public async Task SetAttribute_RefusesToOverwriteARazorExpressionWithoutExpect()
    {
        using var file = new TempRazor("Index.cshtml", "<a href=\"@Model.Url\">x</a>\n");
        var before = file.Read();

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            AttributeName = "href",
            AttributeValue = "/other",
        });

        Assert.Contains("expect", ResultAssert.Error(result));
        Assert.Equal(before, file.Read());
    }

    [Fact]
    public async Task SetAttribute_OverwritesARazorExpressionWhenExpectMatches()
    {
        using var file = new TempRazor("Index.cshtml", "<a href=\"@Model.Url\">x</a>\n");

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            AttributeName = "href",
            AttributeValue = "/other",
            Expect = "@Model.Url",
            DryRun = false,
        });

        ResultAssert.Succeeded(result);
        Assert.Contains("href=\"/other\"", file.Read());
    }

    [Fact]
    public async Task SetAttribute_SelectsTheRequestedOccurrence()
    {
        using var file = new TempRazor("Index.cshtml", """
            <a href="/a">one</a>
            <a href="/b">two</a>
            """);

        await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            Nth = 2,
            AttributeName = "title",
            AttributeValue = "second",
            DryRun = false,
        });

        var text = file.Read();
        Assert.Contains("<a href=\"/a\">one</a>", text);
        Assert.Contains("<a href=\"/b\" title=\"second\">two</a>", text);
    }

    [Fact]
    public async Task SetAttribute_ReportsAnOccurrenceOutOfRange()
    {
        using var file = new TempRazor("Index.cshtml", "<a href=\"/a\">one</a>\n");

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            Nth = 2,
            AttributeName = "title",
            AttributeValue = "x",
        });

        Assert.Contains("occurs 1 time", ResultAssert.Error(result));
    }

    [Fact]
    public async Task RemoveAttribute_RemovesTheAttributeAndItsWhitespace()
    {
        using var file = new TempRazor("Index.cshtml", "<div class=\"a\" hidden>x</div>\n");

        await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "remove-attribute",
            Tag = "div",
            AttributeName = "hidden",
            DryRun = false,
        });

        Assert.Equal("<div class=\"a\">x</div>\n", file.Read());
    }

    // ── directives and lines ───────────────────────────────────────────────────

    [Fact]
    public async Task AddDirective_GoesAfterTheLastDirectiveAndIsIdempotent()
    {
        using var file = new TempRazor("Index.cshtml", """
            @page "/products"
            @using MyApp.Data
            <h1>Products</h1>
            """);

        var service = new RazorEditService();
        await service.EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "add-directive",
            Directive = "inject ICartService Cart",
            DryRun = false,
        });

        var text = file.Read();
        Assert.Equal(1, CountOccurrences(text, "@inject ICartService Cart"));
        Assert.True(
            text.IndexOf("@inject", StringComparison.Ordinal) > text.IndexOf("@using", StringComparison.Ordinal),
            text);

        var again = await service.EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "add-directive",
            Directive = "inject ICartService Cart",
            DryRun = false,
        });

        Assert.False(ResultAssert.Json(again).GetProperty("changed").GetBoolean());
        Assert.Equal(1, CountOccurrences(file.Read(), "@inject ICartService Cart"));
    }

    [Fact]
    public async Task ReplaceLines_RequiresTheExpectedText()
    {
        using var file = new TempRazor("Index.cshtml", "<h1>Old</h1>\n<p>Body</p>\n");
        var before = file.Read();

        var service = new RazorEditService();
        var refused = await service.EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "replace-lines",
            FromLine = 1,
            Text = "<h1>New</h1>",
            Expect = "<h1>Other</h1>",
            DryRun = false,
        });

        Assert.Contains("Nothing was written", ResultAssert.Error(refused));
        Assert.Equal(before, file.Read());

        var applied = await service.EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "replace-lines",
            FromLine = 1,
            Text = "<h1>New</h1>",
            Expect = "<h1>Old</h1>",
            DryRun = false,
        });

        ResultAssert.Succeeded(applied);
        Assert.Equal("<h1>New</h1>\n<p>Body</p>\n", file.Read());
    }

    [Fact]
    public async Task DeleteLines_RemovesTheRangeAndKeepsTheRest()
    {
        using var file = new TempRazor("Index.cshtml", "<h1>Title</h1>\n<!-- remove me -->\n<p>Body</p>\n");

        await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "delete-lines",
            FromLine = 2,
            DryRun = false,
        });

        Assert.Equal("<h1>Title</h1>\n<p>Body</p>\n", file.Read());
    }

    [Fact]
    public async Task InsertLines_BeforeALineInsertsAboveIt()
    {
        using var file = new TempRazor("Index.cshtml", "<h1>Title</h1>\n");

        await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "insert-lines",
            FromLine = 1,
            Position = "before",
            Text = "@{ var title = \"x\"; }",
            DryRun = false,
        });

        Assert.Equal("@{ var title = \"x\"; }\n<h1>Title</h1>\n", file.Read());
    }

    // ── the gate and the refusals ─────────────────────────────────────────────

    [Fact]
    public async Task Edit_RefusesAnEditThatWouldBreakTheMarkup()
    {
        using var file = new TempRazor("Index.cshtml", "<div>x</div>\n");
        var before = file.Read();

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "replace-lines",
            FromLine = 1,
            Text = "<div>x\n<div>y",
            DryRun = false,
        });

        Assert.Contains("unbalanced-syntax", ResultAssert.Error(result));
        Assert.Equal(before, file.Read());
    }

    [Fact]
    public async Task Edit_ReportsTheSupportedOperationsForAnUnknownOne()
    {
        using var file = new TempRazor("Index.cshtml", "<p>x</p>\n");

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "rename-page",
        });

        Assert.Contains("Unknown operation", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Edit_RefusesAFileThatIsNotRazor()
    {
        using var file = new TempRazor("Index.cs", "class C { }\n");

        var result = await new RazorEditService().EditAsync(new RazorEditRequest
        {
            Path = file.Path,
            Operation = "set-attribute",
            Tag = "a",
            AttributeName = "rel",
        });

        Assert.Contains("not a .cshtml or .razor", ResultAssert.Error(result));
    }

    [Fact]
    public void AddSnitch_RegistersTheRazorEditService()
    {
        var services = new ServiceCollection();
        Snitch.SnitchServiceCollectionExtensions.AddSnitch(services);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IRazorEditService) &&
            descriptor.ImplementationType == typeof(RazorEditService));
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A real file in a temp folder: the service writes through its normal path.</summary>
    private sealed class TempRazor : IDisposable
    {
        private readonly string _directory;

        public TempRazor(string name, string content)
        {
            _directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vmcp-razor-edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, name);
            File.WriteAllText(Path, content.Replace("\r\n", "\n"));
        }

        public string Path { get; }

        public string Read() => File.ReadAllText(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
