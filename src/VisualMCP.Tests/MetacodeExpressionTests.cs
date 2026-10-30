using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Generation;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The expressions the model gained so the library's own source translates without RAW: a
/// <c>with</c> expression, an anonymous object, a pattern kept as text (both as a match and as a case
/// label) and a spread element. Each one prints in the canonical text and reads back exactly.
/// </summary>
public sealed class MetacodeExpressionTests
{
    private static string RenderClean(string source)
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(source, "sample.cs"));
        Assert.Empty(document.Diagnostics);
        return new PseudocodeRenderer().Render(document);
    }

    private static string RoundTrip(string pseudocode)
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(pseudocode, "sample.mc"));
        Assert.False(document.HasErrors, string.Join("\n", document.Diagnostics));
        return new PseudocodeRenderer().Render(document);
    }

    [Fact]
    public void A_with_expression_is_a_copy_with_replacements()
    {
        var pseudocode = RenderClean("""
            public sealed record Person
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }

            public static class Names
            {
                public static Person Rename(Person person, string name) => person with { Name = name };
            }
            """);

        Assert.Contains("person WITH { Name = name }", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void An_anonymous_object_keeps_its_member_names()
    {
        var pseudocode = RenderClean("""
            public static class Shapes
            {
                public static object Describe(int x) => new { x, Twice = x * 2 };
            }
            """);

        Assert.Contains("ANON { x = x, Twice = x * 2 }", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_pattern_match_keeps_the_pattern_text()
    {
        var pseudocode = RenderClean("""
            public static class Checks
            {
                public static bool Allowed(string name) => name is not ("admin" or "root");

                public static bool Present(object value) => value is { };

                public static bool InRange(int value) => value is > 0 and < 10;
            }
            """);

        Assert.Contains("name MATCHES (not (\"admin\" or \"root\"))", pseudocode);
        Assert.Contains("value MATCHES ({ })", pseudocode);
        Assert.Contains("value MATCHES (> 0 and < 10)", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_case_pattern_is_a_real_label_not_opaque_text()
    {
        var pseudocode = RenderClean("""
            public static class Choices
            {
                public static string Classify(int value)
                {
                    switch (value)
                    {
                        case > 0:
                            return "positive";
                        case 0:
                            return "zero";
                        default:
                            return "negative";
                    }
                }
            }
            """);

        Assert.Contains("CASE PATTERN (> 0)", pseudocode);
        Assert.Contains("CASE 0", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void A_spread_element_is_kept()
    {
        var pseudocode = RenderClean("""
            public static class Arrays
            {
                public static int[] Combine(int[] values) => [.. values, 42];
            }
            """);

        Assert.Contains("SPREAD values", pseudocode);
        Assert.Equal(pseudocode, RoundTrip(pseudocode));
    }

    [Fact]
    public void The_new_expressions_emit_parseable_csharp()
    {
        const string source = """
            public sealed record Person
            {
                public string Name { get; set; } = "";
            }

            public static class Shapes
            {
                public static Person Rename(Person person, string name) => person with { Name = name };

                public static object Describe(int x) => new { x, Twice = x * 2 };

                public static bool Check(string name) => name is not ("admin" or "root");

                public static int[] Combine(int[] values) => [.. values, 42];
            }
            """;

        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(source, "sample.cs"));
        var emitted = new CSharpEmitter(CSharpEmitOptions.Default with { IncludeUsings = false }).Emit(document);

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(emitted.Code);
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        Assert.Empty(errors);

        Assert.Contains("with { Name = name }", emitted.Code);
        Assert.Contains("new { x = x, Twice = x * 2 }", emitted.Code);
        Assert.Contains("is not (\"admin\" or \"root\")", emitted.Code);
        Assert.Contains("..values", emitted.Code);

        var reparsed = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(emitted.Code, "sample.cs"));
        Assert.Equal(new PseudocodeRenderer().Render(document), new PseudocodeRenderer().Render(reparsed));
    }
}
