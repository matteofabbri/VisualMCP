using Microsoft.CodeAnalysis;

namespace Snitch.Languages.Style;

/// <summary>What <c>csharp_prefer_braces</c> asks for.</summary>
public enum BracePreference
{
    /// <summary>Not configured: the engine leaves braces alone.</summary>
    None,
    Always,
    WhenMultiline,
}

/// <summary>What <c>csharp_style_namespace_declarations</c> asks for.</summary>
public enum NamespacePreference
{
    /// <summary>Not configured: the engine leaves the declaration form alone.</summary>
    None,
    BlockScoped,
    FileScoped,
}

/// <summary>What <c>dotnet_style_prefer_foreach_explicit_cast_in_source</c> asks for.</summary>
public enum ForeachCastPreference
{
    /// <summary>Not configured: the engine leaves the foreach alone.</summary>
    None,
    Always,
    WhenStronglyTyped,
}

/// <summary>
/// What <c>visual_basic_style_prefer_isnot_expression</c> asks for. The option is Visual Basic's, but it
/// lives here for the same reason the brace preference does: a language maps its own key onto a neutral
/// field, and the rule that consumes it never reads an <c>.editorconfig</c> key by name.
/// </summary>
public enum IsNotPreference
{
    /// <summary>Not configured: the engine leaves the comparison alone.</summary>
    None,
    /// <summary>Prefer <c>o IsNot other</c> over <c>Not (o Is other)</c>.</summary>
    PreferIsNot,
    /// <summary>Prefer <c>Not (o Is other)</c> over <c>o IsNot other</c>.</summary>
    PreferNot,
}

/// <summary>What <c>dotnet_style_require_accessibility_modifiers</c> asks for.</summary>
public enum AccessibilityPreference
{
    /// <summary>Not configured: the engine leaves the modifiers alone.</summary>
    None,
    Always,
    ForNonInterfaceMembers,
    OmitIfDefault,
}

/// <summary>
/// The repository's style options, already mapped out of a language's key names. Each language maps its
/// own <c>.editorconfig</c> keys onto these fields (<c>csharp_prefer_braces</c>, <c>visual_basic_…</c>),
/// so the rules and the service stay language-neutral.
/// </summary>
public sealed record StyleOptions
{
    public static readonly StyleOptions Default = new();

    public BracePreference Braces { get; init; } = BracePreference.None;

    /// <summary>The preferred modifier order, in the order the repository wants them.</summary>
    public IReadOnlyList<string> ModifierOrder { get; init; } = [];

    public NamespacePreference Namespaces { get; init; } = NamespacePreference.None;

    public ForeachCastPreference ForeachExplicitCast { get; init; } = ForeachCastPreference.None;

    public AccessibilityPreference Accessibility { get; init; } = AccessibilityPreference.None;

    /// <summary>The <c>IsNot</c> preference of a Visual Basic repository.</summary>
    public IsNotPreference PreferIsNot { get; init; } = IsNotPreference.None;

    /// <summary>
    /// The file header the repository wants (<c>file_header_template</c>), already resolved: the
    /// placeholders a language supports are substituted when the option is read, because the rewrite
    /// that inserts it only sees a document's syntax.
    /// </summary>
    public string? FileHeader { get; init; }

    /// <summary>Whether any option that needs a preference was actually configured.</summary>
    public bool AnyConfigured =>
        Braces != BracePreference.None || ModifierOrder.Count > 0 || Namespaces != NamespacePreference.None ||
        ForeachExplicitCast != ForeachCastPreference.None || Accessibility != AccessibilityPreference.None ||
        PreferIsNot != IsNotPreference.None || !string.IsNullOrWhiteSpace(FileHeader);
}

/// <summary>
/// What a rewrite sees: the semantic model, the repository's options, the workspace the formatter needs,
/// and the solution for the few rules whose answer lives outside the document.
/// </summary>
public sealed class StyleContext(
    SemanticModel model,
    StyleOptions options,
    Microsoft.CodeAnalysis.Workspace? workspace,
    Microsoft.CodeAnalysis.Solution? solution,
    CancellationToken cancellationToken,
    bool pristine = true)
{
    public SemanticModel Model { get; } = model;

    public StyleOptions Options { get; } = options;

    public Microsoft.CodeAnalysis.Workspace? Workspace { get; } = workspace;

    /// <summary>
    /// The whole solution, for the few rules whose shape cannot be decided inside one document —
    /// "is this private member used anywhere" needs every file, not the node in hand.
    /// </summary>
    public Microsoft.CodeAnalysis.Solution? Solution { get; } = solution;

    /// <summary>
    /// True only for the first pass, where the tree being rewritten is still the one the solution
    /// holds. From the second pass the model describes an edited tree the solution knows nothing
    /// about, so a symbol taken from it no longer matches the solution's and the symbol finder
    /// answers "no references" for something that is used.
    /// </summary>
    public bool IsPristine { get; } = pristine;

    public CancellationToken CancellationToken { get; } = cancellationToken;
}
