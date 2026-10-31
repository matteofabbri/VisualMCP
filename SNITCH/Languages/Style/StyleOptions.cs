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
/// What <c>dotnet_style_qualification_for_*</c> asks for one member kind: <c>Me.</c> in Visual Basic,
/// <c>this.</c> in C#. IDE0009 writes the qualifier where the option is <c>true</c>; the opposite
/// preference is what IDE0003 already does unconditionally.
/// </summary>
public enum QualificationPreference
{
    /// <summary>Not configured: the engine leaves the qualification alone.</summary>
    None,
    /// <summary>Prefer the qualified form (<c>Me.X</c>, <c>this.X</c>).</summary>
    PreferQualified,
    /// <summary>Prefer the unqualified form.</summary>
    PreferUnqualified,
}

/// <summary>The four <c>dotnet_style_qualification_for_*</c> categories.</summary>
public sealed record QualificationStyle
{
    public QualificationPreference Field { get; init; } = QualificationPreference.None;

    public QualificationPreference Property { get; init; } = QualificationPreference.None;

    public QualificationPreference Method { get; init; } = QualificationPreference.None;

    public QualificationPreference Event { get; init; } = QualificationPreference.None;

    public bool Any => Field != QualificationPreference.None || Property != QualificationPreference.None ||
                       Method != QualificationPreference.None || Event != QualificationPreference.None;
}

/// <summary>What <c>csharp_style_var_*</c> asks for in one declaration category.</summary>
public enum VarPreference
{
    /// <summary>Not configured: the engine leaves the declaration alone.</summary>
    None,
    /// <summary>Prefer <c>var</c> (the option is true): IDE0007.</summary>
    PreferVar,
    /// <summary>Prefer the explicit type (the option is false): IDE0008.</summary>
    PreferExplicit,
}

/// <summary>The three <c>csharp_style_var_*</c> categories, in Microsoft's own precedence.</summary>
public sealed record VarStyle
{
    public VarPreference ForBuiltInTypes { get; init; } = VarPreference.None;

    public VarPreference WhenTypeIsApparent { get; init; } = VarPreference.None;

    public VarPreference Elsewhere { get; init; } = VarPreference.None;
}

/// <summary>
/// Whether <c>dotnet_style_parentheses_in_*</c> asks for parentheses for clarity around a binary
/// operator family. Only <c>always_for_clarity</c> is a rewrite; <c>never_if_unnecessary</c> is what
/// IDE0047 already does.
/// </summary>
public sealed record ParenthesesStyle
{
    public bool Arithmetic { get; init; }

    public bool Relational { get; init; }

    public bool OtherBinary { get; init; }

    public bool Any => Arithmetic || Relational || OtherBinary;
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

    /// <summary>The <c>dotnet_style_qualification_for_*</c> preferences (IDE0009).</summary>
    public QualificationStyle Qualification { get; init; } = new();

    /// <summary>The <c>csharp_style_var_*</c> preferences (IDE0007/IDE0008).</summary>
    public VarStyle Var { get; init; } = new();

    /// <summary>The <c>dotnet_style_parentheses_in_*</c> preferences (IDE0048).</summary>
    public ParenthesesStyle Parentheses { get; init; } = new();

    /// <summary>
    /// The <c>dotnet_style_allow_*_experimental</c> layout options (IDE2000–IDE2006): null when the
    /// repository did not configure the option, true when it allows the shape, false when it forbids it.
    /// </summary>
    public bool? AllowMultipleBlankLines { get; init; }

    public bool? AllowEmbeddedStatementsOnSameLine { get; init; }

    public bool? AllowBlankLinesBetweenConsecutiveBraces { get; init; }

    public bool? AllowStatementImmediatelyAfterBlock { get; init; }

    public bool? AllowBlankLineAfterColonInConstructorInitializer { get; init; }

    public bool? AllowBlankLineAfterTokenInConditionalExpression { get; init; }

    public bool? AllowBlankLineAfterTokenInArrowExpressionClause { get; init; }

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
        PreferIsNot != IsNotPreference.None || !string.IsNullOrWhiteSpace(FileHeader) ||
        Qualification.Any ||
        Var.ForBuiltInTypes != VarPreference.None || Var.WhenTypeIsApparent != VarPreference.None ||
        Var.Elsewhere != VarPreference.None || Parentheses.Any ||
        AllowMultipleBlankLines is not null || AllowEmbeddedStatementsOnSameLine is not null ||
        AllowBlankLinesBetweenConsecutiveBraces is not null || AllowStatementImmediatelyAfterBlock is not null ||
        AllowBlankLineAfterColonInConstructorInitializer is not null ||
        AllowBlankLineAfterTokenInConditionalExpression is not null ||
        AllowBlankLineAfterTokenInArrowExpressionClause is not null;

    /// <summary>
    /// The header template with its placeholders filled in: <c>\n</c> separates the lines (that is how
    /// the option is written in an <c>.editorconfig</c>), and the tokens the IDE documents are
    /// substituted. A token this engine does not know is left as written, so a template is never silently
    /// altered. Shared by every language: the template is the repository's, not a syntax.
    /// </summary>
    public static string? ResolveHeader(string? template, string? fileName, string? projectName)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        var text = template.Replace("\\n", "\n").Replace("\r\n", "\n");
        text = text
            .Replace("{fileName}", fileName ?? string.Empty)
            .Replace("{fileNameOrFilePath}", fileName ?? string.Empty)
            .Replace("{projectName}", projectName ?? string.Empty)
            .Replace("{copyrightText}", string.Empty);

        return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd('\n');
    }
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
