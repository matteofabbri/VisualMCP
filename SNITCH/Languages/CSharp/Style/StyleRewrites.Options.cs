using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0011: wrap an embedded statement in a block when the repository asks for braces. Without
/// <c>csharp_prefer_braces</c> there is no right answer, so the rule simply does nothing.
/// </summary>
public sealed class BracesRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0011", "Add braces", "if (x) Do(); → if (x) { Do(); }");

    public override string? RequiredOption => "csharp_prefer_braces";

    /// <summary>Without the repository's options there is no decision to make.</summary>
    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preference = context.Options.Braces;
        if (preference == BracePreference.None) return null;
        if (node is not StatementSyntax statement) return null;
        if (ConditionalShape.HasComment(statement)) return null;

        switch (statement)
        {
            case IfStatementSyntax ifStatement:
            {
                var wrapBody = ifStatement.Statement is not BlockSyntax && Wants(preference, ifStatement.Statement);
                var elseClause = ifStatement.Else;
                var wrapElse = elseClause is { Statement: not BlockSyntax and not IfStatementSyntax } &&
                               Wants(preference, elseClause.Statement);
                if (!wrapBody && !wrapElse) return null;

                var replacement = wrapBody ? ifStatement.WithStatement(Braced(ifStatement.Statement)) : ifStatement;
                if (wrapElse)
                {
                    var clause = replacement.Else!;
                    replacement = replacement.WithElse(clause.WithStatement(Braced(clause.Statement)));
                }

                return Match(node, replacement);
            }

            // `using` belongs to IDE0063, which runs first and produces the declaration form.
            case ForStatementSyntax loop when loop.Statement is not BlockSyntax && Wants(preference, loop.Statement):
                return Match(node, loop.WithStatement(Braced(loop.Statement)));

            case ForEachStatementSyntax loop when loop.Statement is not BlockSyntax && Wants(preference, loop.Statement):
                return Match(node, loop.WithStatement(Braced(loop.Statement)));

            case ForEachVariableStatementSyntax loop when loop.Statement is not BlockSyntax && Wants(preference, loop.Statement):
                return Match(node, loop.WithStatement(Braced(loop.Statement)));

            case WhileStatementSyntax loop when loop.Statement is not BlockSyntax && Wants(preference, loop.Statement):
                return Match(node, loop.WithStatement(Braced(loop.Statement)));

            case DoStatementSyntax loop when loop.Statement is not BlockSyntax && Wants(preference, loop.Statement):
                return Match(node, loop.WithStatement(Braced(loop.Statement)));

            case LockStatementSyntax locked when locked.Statement is not BlockSyntax && Wants(preference, locked.Statement):
                return Match(node, locked.WithStatement(Braced(locked.Statement)));

            case FixedStatementSyntax pinned when pinned.Statement is not BlockSyntax && Wants(preference, pinned.Statement):
                return Match(node, pinned.WithStatement(Braced(pinned.Statement)));

            default:
                return null;
        }
    }

    /// <summary>`when_multiline` only braces a body that is itself more than one line.</summary>
    private static bool Wants(BracePreference preference, StatementSyntax body) =>
        preference == BracePreference.Always ||
        (preference == BracePreference.WhenMultiline && body.ToString().Contains('\n'));

    /// <summary>
    /// A block around the statement, with elastic trivia so the formatter decides the layout: the
    /// original indentation belonged to the unbraced line and no longer applies.
    /// </summary>
    private static BlockSyntax Braced(StatementSyntax body) =>
        Block(
            Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(ElasticCarriageReturnLineFeed),
            SingletonList(body.WithoutLeadingTrivia().WithLeadingTrivia(ElasticMarker)),
            Token(SyntaxKind.CloseBraceToken));
}

/// <summary>
/// IDE0036: put modifiers in the order <c>csharp_preferred_modifier_order</c> declares. The option is
/// the whole point of the rule — repositories disagree, so no order is applied by default.
/// </summary>
public sealed class ModifierOrderRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0036", "Order modifiers", "static public int F() → public static int F()");

    public override string? RequiredOption => "csharp_preferred_modifier_order";

    /// <summary>Without the repository's options there is no order to apply.</summary>
    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preference = context.Options.ModifierOrder;
        if (preference.Count == 0) return null;

        var modifiers = ModifiersOf(node);
        if (modifiers is not { Count: > 1 } list) return null;

        // A comment between modifiers would be carried away by the reorder.
        if (list.Any(token => HasComment(token.LeadingTrivia) || HasComment(token.TrailingTrivia))) return null;

        var ordered = list
            .Select((token, position) => (token, position, rank: Rank(preference, token)))
            .OrderBy(entry => entry.rank)
            .ThenBy(entry => entry.position)
            .Select(entry => entry.token.WithLeadingTrivia(entry.token.LeadingTrivia).WithTrailingTrivia(entry.token.TrailingTrivia))
            .ToList();

        if (list.Select(token => token.Text).SequenceEqual(ordered.Select(token => token.Text))) return null;

        var replaced = WithModifiers(node, TokenList(ordered));
        return replaced is null ? null : Match(node, replaced);
    }

    private static int Rank(IReadOnlyList<string> preference, SyntaxToken token)
    {
        var index = preference.ToList().IndexOf(token.Text.ToLowerInvariant());
        return index < 0 ? int.MaxValue : index;
    }

    private static bool HasComment(SyntaxTriviaList trivia) =>
        trivia.Any(item => item.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                           item.IsKind(SyntaxKind.MultiLineCommentTrivia));

    private static SyntaxTokenList? ModifiersOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax declaration => declaration.Modifiers,
        BaseMethodDeclarationSyntax method => method.Modifiers,
        BasePropertyDeclarationSyntax property => property.Modifiers,
        BaseFieldDeclarationSyntax field => field.Modifiers,
        DelegateDeclarationSyntax deleg => deleg.Modifiers,
        _ => null,
    };

    private static SyntaxNode? WithModifiers(SyntaxNode node, SyntaxTokenList modifiers) => node switch
    {
        BaseTypeDeclarationSyntax declaration => declaration.WithModifiers(modifiers),
        BaseMethodDeclarationSyntax method => method.WithModifiers(modifiers),
        BasePropertyDeclarationSyntax property => property.WithModifiers(modifiers),
        BaseFieldDeclarationSyntax field => field.WithModifiers(modifiers),
        DelegateDeclarationSyntax deleg => deleg.WithModifiers(modifiers),
        _ => null,
    };
}

/// <summary>
/// IDE0160 / IDE0161: the namespace declaration form <c>csharp_style_namespace_declarations</c> picks.
/// Only a top-level namespace can become file-scoped: the form is not allowed inside another one.
/// </summary>
public sealed class NamespaceStyleRewrite : CSharpStyleRewrite
{
    private static readonly StyleRule BlockScoped = new(
        "IDE0160", "Use block-scoped namespace",
        "namespace X; → namespace X { … }");

    private static readonly StyleRule FileScoped = new(
        "IDE0161", "Use file-scoped namespace",
        "namespace X { … } → namespace X;");

    public override StyleRule Rule => FileScoped;

    public override IReadOnlyList<StyleRule> Rules => [BlockScoped, FileScoped];

    public override string? RequiredOption => "csharp_style_namespace_declarations";

    /// <summary>Without the repository's options there is no form to prefer.</summary>
    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        switch (context.Options.Namespaces)
        {
            case NamespacePreference.FileScoped when
                node is NamespaceDeclarationSyntax block && block.Parent is CompilationUnitSyntax:
            {
                var replacement = FileScopedNamespaceDeclaration(block.Name)
                    .WithExterns(block.Externs)
                    .WithUsings(block.Usings)
                    .WithMembers(block.Members)
                    .WithAttributeLists(block.AttributeLists)
                    .WithLeadingTrivia(block.GetLeadingTrivia())
                    .WithTrailingTrivia(block.GetTrailingTrivia());

                return Match(FileScoped, node, replacement);
            }

            case NamespacePreference.BlockScoped when node is FileScopedNamespaceDeclarationSyntax file:
            {
                var replacement = NamespaceDeclaration(file.Name)
                    .WithExterns(file.Externs)
                    .WithUsings(file.Usings)
                    .WithMembers(file.Members)
                    .WithAttributeLists(file.AttributeLists)
                    .WithLeadingTrivia(file.GetLeadingTrivia())
                    .WithTrailingTrivia(file.GetTrailingTrivia());

                return Match(BlockScoped, node, replacement);
            }

            default:
                return null;
        }
    }
}

/// <summary>
/// IDE0073: the header <c>file_header_template</c> asks for, inserted where the file has none. Only
/// insertion, never replacement: a header that is already there is the repository's own text, and
/// overwriting it could destroy a licence notice.
/// </summary>
public sealed class FileHeaderRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0073", "Add the file header", "insert file_header_template when the file has none");

    public override string? RequiredOption => "file_header_template";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not CompilationUnitSyntax unit) return null;

        var header = context.Options.FileHeader;
        if (string.IsNullOrWhiteSpace(header)) return null;

        var lines = header.Split('\n');
        var leading = unit.GetLeadingTrivia();

        // Already headed: the first line of the template is there as the file's own leading text.
        if (leading.ToFullString().Contains(lines[0].Trim(), StringComparison.Ordinal))
            return null;

        var comment = string.Join("\n", lines.Select(line => ("// " + line.TrimEnd()).TrimEnd())) + "\n\n";
        var replacement = unit.WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia(comment).AddRange(leading));

        return new StyleMatch(
            Rule, node, replacement, 1,
            "(no header)",
            comment.TrimEnd());
    }
}

/// <summary>
/// IDE0055: formatting. Nothing is invented here — Roslyn's formatter already reads the project's
/// <c>.editorconfig</c> formatting options, which is exactly what the rule asks for. It runs only when
/// it would change something, so an already-formatted file produces no match at all.
/// </summary>
public sealed class FormattingRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0055", "Fix formatting", "re-indent and re-space the file as the .editorconfig asks");

    /// <summary>Formatting rewrites whitespace across the whole file: it is asked for, not swept up by "all".</summary>
    public override bool IsOptIn => true;

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not CompilationUnitSyntax unit) return null;
        if (context.Workspace is null) return null;

        SyntaxNode formatted;
        try
        {
            formatted = Formatter.Format(unit, context.Workspace, options: null, context.CancellationToken);
        }
        catch (Exception)
        {
            return null;
        }

        // IsEquivalentTo ignores trivia, so a formatting-only change would look "equal" here.
        return formatted.ToFullString() == unit.ToFullString() ? null : Match(node, formatted);
    }
}
