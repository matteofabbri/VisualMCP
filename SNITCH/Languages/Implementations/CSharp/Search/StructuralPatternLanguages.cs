using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using SyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Search;

/// <summary>How a pattern was parsed: an expression, a statement or a member declaration.</summary>
internal enum PatternMode { Expression, Statement, Member }

/// <summary>A parsed pattern: the tree with holes, how it was parsed, and the holes it names.</summary>
internal sealed record ParsedPattern(
    SyntaxNode Root,
    PatternMode Mode,
    IReadOnlyList<string> Placeholders,
    bool IsExpressionWildcard,
    bool IsStatementWildcard);

/// <summary>
/// The language half of structural search and replace: how a pattern is parsed into that language's
/// syntax, which nodes can match, what a hole looks like, and how a replacement is parsed back. The
/// matching itself is language-neutral — it compares kinds, tokens and holes — so a language is a
/// frontend, not a second search engine.
/// </summary>
internal interface IStructuralPatternLanguage
{
    /// <summary>The id a caller uses to select the language: <c>cs</c> or <c>vb</c>.</summary>
    string Id { get; }

    string Name { get; }

    bool Handles(SyntaxTree tree);

    /// <summary>Parses a pattern, or returns null with the language-specific reason.</summary>
    ParsedPattern? Parse(string text, out string? error);

    /// <summary>The nodes a pattern can match.</summary>
    IEnumerable<SyntaxNode> Candidates(SyntaxNode root, ParsedPattern pattern);

    /// <summary>Whether a pattern node is a hole standing for any node (a name, a whole statement).</summary>
    bool IsHoleNode(SyntaxNode node, out string name);

    /// <summary>Whether a pattern token is a hole standing for any token (the name of a declaration).</summary>
    bool IsHoleToken(SyntaxToken token, out string name);

    /// <summary>Parses substituted replacement text as the same kind of node the match was.</summary>
    SyntaxNode? ParseLike(SyntaxNode original, string text);

    string Describe(SyntaxNode node);
}

/// <summary>
/// The marker a hole is parsed under. <c>$</c> is not an identifier character in either language, so
/// the dollars are replaced by this prefix before parsing and every occurrence of it in the tree is a
/// hole.
/// </summary>
internal static class StructuralHoles
{
    public const string Prefix = "__ssr_";

    public static readonly Regex Pattern = new(@"\$([A-Za-z_][A-Za-z0-9_]*)\$", RegexOptions.Compiled);

    public static string Replace(string text) =>
        Pattern.Replace(text, match => Prefix + match.Groups[1].Value);

    public static bool IsHole(string text) => text.StartsWith(Prefix, StringComparison.Ordinal);

    public static string NameOf(string text) => text[Prefix.Length..];

    public static List<string> PlaceholdersOf(string text) => Pattern.Matches(text)
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

/// <summary>C#: a statement carries its own semicolon, an expression does not, and a member is a declaration.</summary>
internal sealed class CSharpPatternLanguage : IStructuralPatternLanguage
{
    public string Id => "cs";

    public string Name => "C#";

    public bool Handles(SyntaxTree tree) => tree.Options.Language == LanguageNames.CSharp;

    public ParsedPattern? Parse(string text, out string? error)
    {
        error = null;
        var normalized = StructuralHoles.Replace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "The pattern is empty.";
            return null;
        }

        var placeholders = StructuralHoles.PlaceholdersOf(text);

        if (SyntaxFactory.ParseStatement(normalized) is { } statement && !statement.ContainsDiagnostics)
        {
            var isStatementWildcard =
                statement is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax
                {
                    Expression: Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier,
                } && StructuralHoles.IsHole(identifier.Identifier.ValueText);

            return new ParsedPattern(statement, PatternMode.Statement, placeholders,
                IsExpressionWildcard: false, isStatementWildcard);
        }

        if (SyntaxFactory.ParseExpression(normalized) is { } expression && !expression.ContainsDiagnostics)
        {
            var isExpressionWildcard =
                expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier &&
                StructuralHoles.IsHole(identifier.Identifier.ValueText);

            return new ParsedPattern(expression, PatternMode.Expression, placeholders,
                isExpressionWildcard, IsStatementWildcard: false);
        }

        if (SyntaxFactory.ParseMemberDeclaration(normalized) is { } member && !member.ContainsDiagnostics)
            return new ParsedPattern(member, PatternMode.Member, placeholders,
                IsExpressionWildcard: false, IsStatementWildcard: false);

        error = $"'{text}' is not a valid C# expression, statement or member declaration. " +
                "Write a hole as $name$ (for example: Console.WriteLine($argument$)); a hole in statement " +
                "position carries its own semicolon (for example: if ($condition$) $body$;).";
        return null;
    }

    public IEnumerable<SyntaxNode> Candidates(SyntaxNode root, ParsedPattern pattern)
    {
        if (pattern.IsExpressionWildcard)
            return root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax>();

        if (pattern.IsStatementWildcard)
            return root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();

        return root.DescendantNodes().Where(node => node.RawKind == pattern.Root.RawKind);
    }

    public bool IsHoleNode(SyntaxNode node, out string name)
    {
        if (node is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier &&
            StructuralHoles.IsHole(identifier.Identifier.ValueText))
        {
            name = StructuralHoles.NameOf(identifier.Identifier.ValueText);
            return true;
        }

        // `$body$` in a statement position stands for one statement of any shape, a block included.
        if (node is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax
            {
                Expression: Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax hole,
            } && StructuralHoles.IsHole(hole.Identifier.ValueText))
        {
            name = StructuralHoles.NameOf(hole.Identifier.ValueText);
            return true;
        }

        name = string.Empty;
        return false;
    }

    public bool IsHoleToken(SyntaxToken token, out string name)
    {
        if (StructuralHoles.IsHole(token.ValueText))
        {
            name = StructuralHoles.NameOf(token.ValueText);
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>
    /// The replacement is parsed as the same kind of node the match was, so a substituted statement
    /// stays a statement and a substituted expression cannot grow into one.
    /// </summary>
    public SyntaxNode? ParseLike(SyntaxNode original, string text) => original switch
    {
        Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax => SyntaxFactory.ParseStatement(text),
        Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax => SyntaxFactory.ParseExpression(text),
        Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax => SyntaxFactory.ParseMemberDeclaration(text),
        _ => null,
    };

    public string Describe(SyntaxNode node) => node switch
    {
        Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax => "statement",
        Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax => "expression",
        Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax => "member declaration",
        _ => "node",
    };
}

/// <summary>
/// Visual Basic: there is no trailing semicolon, so a pattern is tried as an expression first — an
/// invocation matches wherever it appears — then as a statement, then as a member. A statement pattern
/// is parsed inside a method body and an expression pattern inside a function's <c>Return</c>, because
/// VB has no free-standing "parse a statement" entry point.
/// </summary>
internal sealed class VisualBasicPatternLanguage : IStructuralPatternLanguage
{
    public string Id => "vb";

    public string Name => "Visual Basic";

    public bool Handles(SyntaxTree tree) => tree.Options.Language == LanguageNames.VisualBasic;

    public ParsedPattern? Parse(string text, out string? error)
    {
        error = null;
        var normalized = StructuralHoles.Replace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "The pattern is empty.";
            return null;
        }

        var placeholders = StructuralHoles.PlaceholdersOf(text);

        if (ParseExpression(normalized) is { } expression)
        {
            var isExpressionWildcard =
                expression is Microsoft.CodeAnalysis.VisualBasic.Syntax.IdentifierNameSyntax identifier &&
                StructuralHoles.IsHole(identifier.Identifier.ValueText);

            return new ParsedPattern(expression, PatternMode.Expression, placeholders,
                isExpressionWildcard, IsStatementWildcard: false);
        }

        if (ParseStatement(normalized) is { } statement)
        {
            var isStatementWildcard =
                statement is Microsoft.CodeAnalysis.VisualBasic.Syntax.ExpressionStatementSyntax
                {
                    Expression: Microsoft.CodeAnalysis.VisualBasic.Syntax.IdentifierNameSyntax identifier,
                } && StructuralHoles.IsHole(identifier.Identifier.ValueText);

            return new ParsedPattern(statement, PatternMode.Statement, placeholders,
                IsExpressionWildcard: false, isStatementWildcard);
        }

        if (ParseMember(normalized) is { } member)
            return new ParsedPattern(member, PatternMode.Member, placeholders,
                IsExpressionWildcard: false, IsStatementWildcard: false);

        error = $"'{text}' is not a valid Visual Basic expression, statement or member declaration. " +
                "Write a hole as $name$ (for example: Console.WriteLine($argument$)); a hole in statement " +
                "position is a line of its own (for example: If $condition$ Then $body$).";
        return null;
    }

    public IEnumerable<SyntaxNode> Candidates(SyntaxNode root, ParsedPattern pattern)
    {
        if (pattern.IsExpressionWildcard)
            return root.DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.ExpressionSyntax>();

        if (pattern.IsStatementWildcard)
            return root.DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.StatementSyntax>();

        return root.DescendantNodes().Where(node => node.RawKind == pattern.Root.RawKind);
    }

    public bool IsHoleNode(SyntaxNode node, out string name)
    {
        if (node is Microsoft.CodeAnalysis.VisualBasic.Syntax.IdentifierNameSyntax identifier &&
            StructuralHoles.IsHole(identifier.Identifier.ValueText))
        {
            name = StructuralHoles.NameOf(identifier.Identifier.ValueText);
            return true;
        }

        if (node is Microsoft.CodeAnalysis.VisualBasic.Syntax.ExpressionStatementSyntax
            {
                Expression: Microsoft.CodeAnalysis.VisualBasic.Syntax.IdentifierNameSyntax hole,
            } && StructuralHoles.IsHole(hole.Identifier.ValueText))
        {
            name = StructuralHoles.NameOf(hole.Identifier.ValueText);
            return true;
        }

        name = string.Empty;
        return false;
    }

    public bool IsHoleToken(SyntaxToken token, out string name)
    {
        if (StructuralHoles.IsHole(token.ValueText))
        {
            name = StructuralHoles.NameOf(token.ValueText);
            return true;
        }

        name = string.Empty;
        return false;
    }

    public SyntaxNode? ParseLike(SyntaxNode original, string text)
    {
        if (original is Microsoft.CodeAnalysis.VisualBasic.Syntax.ExpressionSyntax)
            return ParseExpression(text);

        // A VB declaration is also a statement, but it does not parse inside a method body: the
        // fallback keeps a type or member match working.
        return ParseStatement(text) ?? ParseMember(text) ?? ParseExpression(text);
    }

    public string Describe(SyntaxNode node) => node switch
    {
        Microsoft.CodeAnalysis.VisualBasic.Syntax.DeclarationStatementSyntax => "member declaration",
        Microsoft.CodeAnalysis.VisualBasic.Syntax.StatementSyntax => "statement",
        Microsoft.CodeAnalysis.VisualBasic.Syntax.ExpressionSyntax => "expression",
        _ => "node",
    };

    // ── parsing ──────────────────────────────────────────────────────────────

    private static SyntaxNode? ParseStatement(string text)
    {
        var root = ParseTree("Sub __ssr_holder()", text, "End Sub");
        var method = root.Members.OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax>().FirstOrDefault();
        var statements = method?.Statements;

        // One statement, exactly: a pattern that is two statements has no single node to match.
        return statements is { Count: 1 } single ? single[0] : null;
    }

    private static SyntaxNode? ParseExpression(string text)
    {
        var root = ParseTree("Function __ssr_holder() As Object", "Return " + text, "End Function");
        var function = root.Members.OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax>().FirstOrDefault();
        var @return = function?.Statements
            .OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.ReturnStatementSyntax>()
            .FirstOrDefault();

        return @return?.Expression;
    }

    private static SyntaxNode? ParseMember(string text)
    {
        var root = ParseTree("Class __ssr_holder", text, "End Class");
        var type = root.Members.OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.TypeBlockSyntax>().FirstOrDefault();
        var members = type?.Members;

        return members is { Count: 1 } single ? single[0] : null;
    }

    /// <summary>Parses a wrapper around the pattern text; a wrapper with a diagnostic means the pattern does not.</summary>
    private static Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax ParseTree(
        string open, string text, string close)
    {
        var tree = VisualBasicSyntaxTree.ParseText(
            open + Environment.NewLine + text + Environment.NewLine + close,
            new VisualBasicParseOptions(Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest));

        return tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            ? Empty()
            : (Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax)tree.GetRoot();

        static Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax Empty() =>
            (Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax)
            VisualBasicSyntaxTree.ParseText(string.Empty).GetRoot();
    }
}
