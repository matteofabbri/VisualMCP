using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;

namespace Snitch.Languages.VB.Style;

/// <summary>
/// What the shared style service needs to know about Visual Basic: which nodes are statements, how a
/// block's statement list is read and rebuilt, and how the <c>visual_basic_*</c> and
/// <c>dotnet_style_*</c> keys map onto the neutral <see cref="StyleOptions"/>. These members are the
/// whole contract; the rest of the pipeline is language-neutral.
/// </summary>
public sealed class VbStyleLanguage : IStyleLanguage
{
    public static readonly VbStyleLanguage Default = new();

    public string Name => LanguageNames.VisualBasic;

    public string Extension => ".vb";

    /// <summary>
    /// VB's <c>StatementSyntax</c> covers declarations too — a method block is a statement here — but a
    /// rule in this engine only ever matches an executable statement or an expression, and the service
    /// asks this question about the replacement it is about to splice.
    /// </summary>
    public bool IsStatement(SyntaxNode node) => node is StatementSyntax;

    /// <summary>
    /// VB has no block wrapper: a rule replaces an expression or a single statement, and a parsed
    /// statement is spliced by the service as itself. A multi-statement replacement has no syntax in this
    /// language, so there is nothing that answers "yes".
    /// </summary>
    public bool IsShapeWrapper(SyntaxNode replacement) => false;

    public IReadOnlyList<SyntaxNode> StatementsOf(SyntaxNode parent) => VbBlocks.StatementsOf(parent);

    public SyntaxNode WithStatements(SyntaxNode parent, IReadOnlyList<SyntaxNode> statements) =>
        VbBlocks.WithStatements(parent, statements);

    public StyleOptions ReadOptions(Document document, SyntaxTree? tree)
    {
        if (tree is null) return StyleOptions.Default;

        try
        {
            var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);
            return Parse(key => options.TryGetValue(key, out var value) ? value : null);
        }
        catch (Exception)
        {
            // A project without analyzer-config support still gets the default behaviour.
            return StyleOptions.Default;
        }
    }

    /// <summary>
    /// The parsing half, separate so it can be tested without a workspace. Only the keys that exist for
    /// VB are read: there is no <c>visual_basic_prefer_braces</c> (no braces) and no VB namespace-form
    /// option (VB has block namespaces only), so <see cref="StyleOptions.Braces"/> and
    /// <see cref="StyleOptions.Namespaces"/> stay at their defaults.
    /// </summary>
    public static StyleOptions Parse(Func<string, string?> option) => new()
    {
        // "Protected Friend" is one modifier, so the split keeps the space inside an entry.
        ModifierOrder = (option("visual_basic_preferred_modifier_order") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(modifier => modifier.ToLowerInvariant())
            .ToList(),
        Accessibility = option("dotnet_style_require_accessibility_modifiers")?.Trim().ToLowerInvariant() switch
        {
            "always" => AccessibilityPreference.Always,
            "for_non_interface_members" => AccessibilityPreference.ForNonInterfaceMembers,
            "omit_if_default" => AccessibilityPreference.OmitIfDefault,
            _ => AccessibilityPreference.None,
        },
        PreferIsNot = option("visual_basic_style_prefer_isnot_expression")?.Trim().ToLowerInvariant() switch
        {
            "true" => IsNotPreference.PreferIsNot,
            "false" => IsNotPreference.PreferNot,
            _ => IsNotPreference.None,
        },
    };
}

/// <summary>The Visual Basic rule set as the shared service sees it: the catalogue and its refusals.</summary>
public sealed class VbStyleRuleSet : IStyleRuleSet
{
    public static readonly VbStyleRuleSet Default = new();

    public IReadOnlyList<StyleRewrite> All => VbStyleRewriteCatalog.All;

    public IReadOnlyList<StyleRule> Supported => VbStyleRewriteCatalog.Supported;

    public IReadOnlyList<(string Id, string Reason)> NotRewritten => VbStyleRewriteCatalog.NotRewritten;

    public string SupportedIds => VbStyleRewriteCatalog.SupportedIds;
}
