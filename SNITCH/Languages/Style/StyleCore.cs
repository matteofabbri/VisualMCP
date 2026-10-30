using Microsoft.CodeAnalysis;

namespace Snitch.Languages.Style;

/// <summary>One style suggestion the engine can apply, with the IDE id it corresponds to.</summary>
/// <param name="ClaimsEquivalence">
/// True when the rewrite is meant to leave the program meaning untouched at the operation level (a
/// name or trivia change, not a different expression). Those rules are verified with
/// <c>SemanticEquivalence</c>: a difference refuses the file instead of being written.
/// </param>
public sealed record StyleRule(string Id, string Title, string Description, bool ClaimsEquivalence = false);

/// <summary>
/// A rule that claims equivalence rewrote something whose operation tree still differs. This is the
/// second level of the gate: compiling is not the same as preserving meaning.
/// </summary>
public sealed record SemanticFailure(string Rule, int Line, string File, IReadOnlyList<string> Differences);

/// <summary>
/// One text edit in another document, part of the same suggestion: the call site that a removed
/// parameter leaves behind, for example. Spans are offsets into that document's current text.
/// </summary>
public sealed record SpanEdit(string FilePath, int Start, int Length, string Replacement = "");

/// <summary>A rewrite found in a document: what it replaces, where, and how it reads before and after.</summary>
/// <param name="Node">The first node of the shape (for multi-statement rewrites, the first statement).</param>
/// <param name="Last">The last node of the shape, when it spans several statements.</param>
/// <param name="Removes">True when the shape is deleted instead of replaced.</param>
/// <param name="SpanEdits">
/// A multi-document suggestion: instead of one node replacement, a set of text edits across files (the
/// declaration and its call sites). The service applies them to the documents they name, gates them
/// together, and refuses the whole match when a target is also being rewritten as a syntax tree.
/// </param>
public sealed record StyleMatch(
    StyleRule Rule, SyntaxNode Node, SyntaxNode Replacement, int Line, string Before, string After,
    string File = "", SyntaxNode? Last = null, bool Removes = false, IReadOnlyList<SpanEdit>? SpanEdits = null);

/// <summary>
/// One mechanical style rewrite. Implementations are deliberately narrow: they match only shapes where
/// the replacement is provably equivalent, and the service still gates the result on the compiler.
/// <para>
/// Nothing here is C#-specific: a rule works on Roslyn syntax nodes and a <see cref="StyleContext"/>,
/// and the same base class serves Visual Basic. The helpers that need a language's own syntax factory
/// (parsing a replacement expression or statement from text) live in the language layer.
/// </para>
/// </summary>
public abstract class StyleRewrite
{
    public abstract StyleRule Rule { get; }

    /// <summary>Every id this rewrite can report (a family such as IDE0028/IDE0300/IDE0301 shares code).</summary>
    public virtual IReadOnlyList<StyleRule> Rules => [Rule];

    /// <summary>
    /// The <c>.editorconfig</c> key this rule needs before it can decide anything, or null when the
    /// rule is unconditional. Used to explain an id that did nothing instead of leaving it silent.
    /// </summary>
    public virtual string? RequiredOption => null;

    /// <summary>
    /// True when the rule is a whole-file pass rather than a local suggestion, so "rules=all" leaves it
    /// out: reformatting every file is something to ask for by id, not a side effect of "all".
    /// </summary>
    public virtual bool IsOptIn => false;

    public abstract StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken);

    /// <summary>
    /// The entry point the engine calls. The default forwards to the model-only overload, so a rule
    /// that needs the repository's options overrides this one and everything else stays as it was.
    /// </summary>
    public virtual StyleMatch? TryMatch(SyntaxNode node, StyleContext context) =>
        TryMatch(node, context.Model, context.CancellationToken);

    protected StyleMatch Match(SyntaxNode node, SyntaxNode replacement) =>
        new(Rule, node, replacement, Line(node), node.ToString(), replacement.ToString());

    protected StyleMatch Match(StyleRule rule, SyntaxNode node, SyntaxNode replacement) =>
        new(rule, node, replacement, Line(node), node.ToString(), replacement.ToString());

    /// <summary>The shape disappears: the node is deleted, leaving no replacement behind.</summary>
    protected StyleMatch Removal(SyntaxNode node) =>
        new(Rule, node, node, Line(node), node.ToString(), string.Empty, Removes: true);

    protected StyleMatch Removal(StyleRule rule, SyntaxNode node) =>
        new(rule, node, node, Line(node), node.ToString(), string.Empty, Removes: true);

    /// <summary>A span of statements disappears, from <paramref name="first"/> through <paramref name="last"/>.</summary>
    protected StyleMatch Removal(SyntaxNode first, SyntaxNode last) =>
        new(Rule, first, first, Line(first), first.ToString(), string.Empty, Last: last, Removes: true);

    /// <summary>
    /// A suggestion whose edits live in several documents: the node it matched is only the anchor the
    /// report points at, and the actual change is the text edits.
    /// </summary>
    protected StyleMatch Shape(StyleRule rule, SyntaxNode anchor, IReadOnlyList<SpanEdit> edits, string before, string after) =>
        new(rule, anchor, anchor, Line(anchor), before, after, SpanEdits: edits);

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    protected static bool SameSymbol(ISymbol? left, ISymbol? right) =>
        left is not null && right is not null && SymbolEqualityComparer.Default.Equals(left, right);

    /// <summary>
    /// True when the operator is user-defined, which is the case these rewrites must leave alone: its
    /// behaviour is not the built-in one the rule is about, so "x is null" or "x" would change it.
    /// </summary>
    protected static bool IsUserDefinedOperator(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(node, cancellationToken).Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator };
}

/// <summary>
/// The rule set of one language: everything it can rewrite, and every id it deliberately does not with
/// the reason. The service is parameterized by this, so a rule set is the only thing a new language has
/// to provide besides its own matchers.
/// </summary>
public interface IStyleRuleSet
{
    IReadOnlyList<StyleRewrite> All { get; }

    IReadOnlyList<StyleRule> Supported { get; }

    IReadOnlyList<(string Id, string Reason)> NotRewritten { get; }

    string SupportedIds { get; }
}

/// <summary>
/// What the service needs to know about a language: how to tell a statement from an expression, how to
/// read a block's statement list and write it back, which extension it owns, and how its options map.
/// Everything else in the pipeline — scope selection, passes, the compiler gate, the report — is shared.
/// </summary>
public interface IStyleLanguage
{
    /// <summary>The language name, as Roslyn's <c>Project.Language</c> reports it.</summary>
    string Name { get; }

    /// <summary>The file extension this language owns, including the dot.</summary>
    string Extension { get; }

    bool IsStatement(SyntaxNode node);

    /// <summary>
    /// True when a replacement is a wrapper holding statements (a parsed block): its statements are
    /// spliced into the enclosing list instead of the wrapper itself.
    /// </summary>
    bool IsShapeWrapper(SyntaxNode replacement);

    /// <summary>The statements of a node that holds a statement list, in order; empty for anything else.</summary>
    IReadOnlyList<SyntaxNode> StatementsOf(SyntaxNode parent);

    /// <summary>Rebuilds a node with a new statement list.</summary>
    SyntaxNode WithStatements(SyntaxNode parent, IReadOnlyList<SyntaxNode> statements);

    /// <summary>Reads this language's style options from the document's <c>.editorconfig</c> chain.</summary>
    StyleOptions ReadOptions(Document document, SyntaxTree? tree);
}
