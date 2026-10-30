using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Collapses C# into the shortest text that still lexes to exactly the same tokens: no indentation,
/// no line breaks between tokens, no comments.
/// <para>
/// ILSpy's output is printed for a human: eight to sixteen spaces of indentation per line and a line
/// per statement, which is roughly a third of the characters. Those characters are tokens a reader
/// pays for and none of them carries meaning, so the decompiler's inline answer goes through here.
/// </para>
/// <para>
/// Whitespace is only removable where it does not change lexing, so a space is kept when two tokens
/// would otherwise merge into one (<c>int x</c>, <c>a + +b</c>, <c>x ? .y</c> …). The result is then
/// re-lexed and compared with the input token by token; if the comparison ever fails (a construct the
/// pair rule does not know) the caller gets the always-safe form with a single space between every
/// token instead of a corrupted one.
/// </para>
/// </summary>
public static class CSharpCompactor
{
    /// <summary>Two-character sequences that mean something else when they are glued together.</summary>
    private static readonly HashSet<string> OperatorPairs = new(StringComparer.Ordinal)
    {
        "++", "+=", "--", "-=", "->", "&&", "&=", "||", "|=",
        "<<", "<=", ">>", ">=", "==", "!=", "=>", "??", "?.", "?[",
        "::", "..", "/*", "//", "*/", "/=",
    };

    /// <summary>The code with every piece of whitespace and every comment that can be dropped, dropped.</summary>
    public static string Compact(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return code ?? string.Empty;

        var tokens = Lex(code);
        if (tokens.Count == 0) return code;

        var minimal = Build(tokens, spaceBetweenAll: false);
        return LexesToTheSameTokens(minimal, tokens) ? minimal : Build(tokens, spaceBetweenAll: true);
    }

    /// <summary>
    /// How much of the text disappears: 1 - compacted length / original length. Reported next to the
    /// code so a caller can see what compaction did, and 0 when the text was already minimal.
    /// </summary>
    public static double RemovedFraction(string code, string compacted) =>
        string.IsNullOrEmpty(code) ? 0 : 1 - (double)compacted.Length / code.Length;

    private static List<SyntaxToken> Lex(string code) =>
        CSharpSyntaxTree.ParseText(code, ParseOptions())
            .GetRoot()
            .DescendantTokens()
            .Where(token => token.RawKind != (int)SyntaxKind.EndOfFileToken)
            .ToList();

    private static CSharpParseOptions ParseOptions() =>
        new(LanguageVersion.Preview, DocumentationMode.None);

    private static string Build(List<SyntaxToken> tokens, bool spaceBetweenAll)
    {
        var text = new StringBuilder(tokens.Sum(token => token.Text.Length));
        foreach (var token in tokens)
        {
            // A directive is not a token and cannot be joined to one: it is emitted verbatim, on its
            // own line. Decompiled assemblies rarely contain one, but a source file may.
            foreach (var trivia in token.LeadingTrivia)
            {
                if (!trivia.IsDirective) continue;
                var directive = trivia.ToFullString().TrimEnd('\r', '\n', ' ', '\t');
                if (directive.Length == 0) continue;

                if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
                text.Append(directive).Append('\n');
            }

            var next = token.Text;
            if (next.Length == 0) continue;

            if (text.Length > 0 && text[^1] != '\n')
            {
                if (spaceBetweenAll) text.Append(' ');
                else if (NeedsSpace(text[^1], next[0])) text.Append(' ');
            }

            text.Append(next);
        }

        return text.ToString();
    }

    /// <summary>Whether two adjacent tokens would lex as one (or as a different operator) without a space.</summary>
    private static bool NeedsSpace(char left, char right)
    {
        if (IsIdentifierPart(left) && IsIdentifierPart(right)) return true;

        // A number followed by '.' starts a numeric literal: '1.ToString()' is not '1 .ToString()'.
        if (char.IsDigit(left) && right == '.') return true;

        return OperatorPairs.Contains(string.Concat(left, right));
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// The gate: the compacted text must parse into the same token sequence it came from. Comparing
    /// parsed tokens (not raw lexing) makes the check agree with the compiler about '&gt;&gt;' in
    /// nested generics, which the parser splits.
    /// </summary>
    private static bool LexesToTheSameTokens(string candidate, List<SyntaxToken> expected)
    {
        var actual = Lex(candidate);
        if (actual.Count != expected.Count) return false;

        for (var i = 0; i < actual.Count; i++)
        {
            if (!string.Equals(actual[i].Text, expected[i].Text, StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
