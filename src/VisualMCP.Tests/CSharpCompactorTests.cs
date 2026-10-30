using Microsoft.CodeAnalysis.CSharp;
using Snitch.Metacode;
using Snitch.Metacode.Decompilation;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The compaction the decompiler applies to the C# it prints: the tokens must survive it exactly, because
/// the answer is read as code.
/// </summary>
public sealed class CSharpCompactorTests
{
    private static int TokenCount(string code) =>
        CSharpSyntaxTree.ParseText(code).GetRoot().DescendantTokens()
            .Count(token => token.RawKind != (int)SyntaxKind.EndOfFileToken);

    private static IReadOnlyList<string> Tokens(string code) =>
        CSharpSyntaxTree.ParseText(code).GetRoot().DescendantTokens()
            .Where(token => token.RawKind != (int)SyntaxKind.EndOfFileToken)
            .Select(token => token.Text)
            .ToList();

    [Fact]
    public void The_compacted_text_lexes_to_exactly_the_same_tokens()
    {
        const string code = """
            namespace Library
            {
                public sealed class Widget : IDisposable
                {
                    private readonly int _count;

                    public Widget(int count) => _count = count;

                    public int Describe(string name)
                    {
                        if (name is null) throw new ArgumentNullException(nameof(name));
                        var total = _count + 1;
                        for (var i = 0; i < total; i++) Console.WriteLine($"{name}: {i}");
                        return total > 0 ? total : -1;
                    }
                }
            }
            """;

        var compacted = CSharpCompactor.Compact(code);

        Assert.Equal(Tokens(code), Tokens(compacted));
        Assert.DoesNotContain("    ", compacted);
        Assert.True(compacted.Length < code.Length * 0.75, $"compaction kept {compacted.Length} of {code.Length} characters");
    }

    [Fact]
    public void Operators_that_would_merge_keep_the_space_that_sets_them_apart()
    {
        // Each of these becomes a different token if the whitespace is simply deleted.
        const string code = """
            class C
            {
                int F(int a, int b, bool c)
                {
                    var x = a + +b;
                    var y = a - -b;
                    var z = a < b ? 1 : 2;
                    var w = c ? true : false;
                    int? n = null;
                    var q = n ?? 0;
                    var r = new List<List<int>>();
                    return x + y + z + (w ? 1 : 0) + q + r.Count;
                }
            }
            """;

        var compacted = CSharpCompactor.Compact(code);
        Assert.Equal(Tokens(code), Tokens(compacted));
    }

    [Fact]
    public void A_number_followed_by_a_member_access_keeps_its_space()
    {
        // '1.ToString()' would lex as the numeric literal '1.' and stop compiling.
        const string code = "class C { string F() => 1 .ToString(); }";

        var compacted = CSharpCompactor.Compact(code);
        Assert.Equal(Tokens(code), Tokens(compacted));
        Assert.Contains("1 .ToString()", compacted);
    }

    [Fact]
    public void Comments_disappear_and_a_directive_keeps_its_own_line()
    {
        const string code = """
            // a note that explains nothing a token says
            class C
            {
                /// <summary>Documented.</summary>
                int F() => 1; // trailing
            #if DEBUG
                int G() => 2;
            #endif
            }
            """;

        var compacted = CSharpCompactor.Compact(code);
        var tokens = Tokens(code);

        Assert.Equal(tokens, Tokens(compacted));
        Assert.DoesNotContain("note", compacted);
        Assert.DoesNotContain("<summary>", compacted);
        Assert.Contains("#if DEBUG\n", compacted);
        Assert.Contains("#endif\n", compacted);
    }

    [Fact]
    public void A_verbatim_string_keeps_its_own_whitespace()
    {
        const string code = """
            class C
            {
                string F() => @"line one
            line two";
            }
            """;

        var compacted = CSharpCompactor.Compact(code);
        Assert.Equal(Tokens(code), Tokens(compacted));

        // The text of a verbatim literal is content, not formatting: it survives the compaction.
        Assert.Contains("line one\nline two", compacted.Replace("\r\n", "\n"));
    }

    [Fact]
    public void An_empty_or_already_minimal_text_is_returned_unchanged()
    {
        Assert.Equal(string.Empty, CSharpCompactor.Compact(string.Empty));
        Assert.Equal("class C{}", CSharpCompactor.Compact("class C{}"));
        Assert.True(TokenCount("class C{}") > 0);
    }

    [Fact]
    public void Real_ILSpy_output_compacts_to_exactly_the_same_tokens()
    {
        // The rule is only worth trusting on what the decompiler actually prints: decompile one real
        // type twice and compare the two token sequences.
        var assembly = typeof(MetacodeTranslator).Assembly.Location;
        var service = new DecompileService();

        var readable = Json(service.DecompileType(assembly, "Snitch.Metacode.Decompilation.DecompileService", 190_000, compact: false));
        var compacted = Json(service.DecompileType(assembly, "Snitch.Metacode.Decompilation.DecompileService", 190_000, compact: true));

        var full = readable.GetProperty("code").GetString()!;
        var small = compacted.GetProperty("code").GetString()!;

        Assert.True(compacted.GetProperty("compacted").GetBoolean());
        Assert.Equal(Tokens(full), Tokens(small));

        // Measured on this type: about a sixth of the characters. The saving is whatever the
        // indentation was — short lines save less, deeply nested code saves more — so the floor is
        // "some", not a number that would fail the day ILSpy changes its formatting.
        Assert.True(small.Length < full.Length * 0.9,
            $"compaction kept {small.Length} of {full.Length} characters");
    }

    private static System.Text.Json.JsonElement Json(object result) =>
        System.Text.Json.JsonSerializer.SerializeToElement(result);
}
