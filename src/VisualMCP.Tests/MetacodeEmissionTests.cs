using Snitch.Metacode;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The one direction an edit may take. Metacode reads every language a frontend understands and stores
/// the result in its own AST; producing source is a separate on-demand step that exists only where an
/// emitter exists — C# today. So an edit can land in a C# file and nowhere else, and that is the same rule
/// for a Razor page, a VB module and a JavaScript file: none of them is a special case, they are inputs.
/// </summary>
public sealed class MetacodeEmissionTests
{
    [Theory]
    [InlineData("Services/OrderService.cs")]
    [InlineData("Pages/Index.cshtml.cs")]     // the code-behind model: ordinary C#
    [InlineData("C.CS")]
    [InlineData(@"C:\app\Program.cs")]
    public void ACSharpFileIsTheOnlyWriteTarget(string path)
    {
        Assert.True(MetacodeEmission.IsTargetFile(path));
        Assert.True(MetacodeEmission.CanWriteTo(path));
        Assert.Null(MetacodeEmission.Refusal(path));
    }

    [Theory]
    [InlineData("Pages/Index.cshtml")]
    [InlineData("Components/Counter.razor")]
    [InlineData("Module.vb")]
    [InlineData("Demo.java")]
    [InlineData("app.js")]
    [InlineData("lib.rs")]
    [InlineData("schema.sql")]
    [InlineData("tool.py")]
    [InlineData(null)]
    public void EveryInputLanguageIsReadOnlyForAnEdit(string? path)
    {
        Assert.False(MetacodeEmission.IsTargetFile(path));
        Assert.False(MetacodeEmission.CanWriteTo(path));

        var refusal = MetacodeEmission.Refusal(path);
        Assert.NotNull(refusal);
        Assert.Contains("is not a C# file", refusal, StringComparison.Ordinal);
        Assert.Contains("emitter", refusal, StringComparison.Ordinal);

        if (path is not null)
            Assert.Contains(Path.GetFileName(path), refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelEmitsCSharpAndNothingElse()
    {
        Assert.Equal(MetacodeLanguage.CSharp, MetacodeEmission.Target);
    }

    [Fact]
    public void EveryLanguageIsStillAnInput()
    {
        // The other half of the rule: the frontends keep reading, Razor included — it is one language among
        // the others, and being un-writable says nothing about being un-readable.
        var razor = MetacodeTranslator.Default.Translate(
            MetacodeSource.FromRazor("@page\n<h1>@Model.Title</h1>\n", "Pages/Index.cshtml"));

        Assert.Equal(MetacodeLanguage.Razor, razor.Language);
        Assert.NotEmpty(razor.Modules);

        var javascript = MetacodeTranslator.Default.Translate(
            MetacodeSource.FromJavaScript("export function twice(x) { return x * 2; }\n", "app.js"));

        Assert.Equal(MetacodeLanguage.JavaScript, javascript.Language);
        Assert.NotEmpty(javascript.Modules);
    }
}
