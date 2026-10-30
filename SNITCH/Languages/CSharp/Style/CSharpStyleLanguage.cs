using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.Style;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// What the shared style service needs to know about C#: which nodes are statements, how a block's
/// statement list is read and rebuilt, and how the <c>csharp_*</c> editor-config keys map onto the
/// neutral <see cref="StyleOptions"/>. A Visual Basic implementation provides the same four things.
/// </summary>
public sealed class CSharpStyleLanguage : IStyleLanguage
{
    public static readonly CSharpStyleLanguage Default = new();

    public string Name => LanguageNames.CSharp;

    public string Extension => ".cs";

    public bool IsStatement(SyntaxNode node) => node is StatementSyntax;

    /// <summary>A parsed block is a wrapper: its statements are spliced, not the block node itself.</summary>
    public bool IsShapeWrapper(SyntaxNode replacement) => replacement is BlockSyntax;

    public IReadOnlyList<SyntaxNode> StatementsOf(SyntaxNode parent) =>
        parent is BlockSyntax block ? block.Statements.Cast<SyntaxNode>().ToList() : [];

    public SyntaxNode WithStatements(SyntaxNode parent, IReadOnlyList<SyntaxNode> statements) =>
        ((BlockSyntax)parent).WithStatements(SyntaxFactory.List(statements.Cast<StatementSyntax>()));

    public StyleOptions ReadOptions(Document document, SyntaxTree? tree)
    {
        if (tree is null) return StyleOptions.Default;

        try
        {
            var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);
            return Parse(
                key => options.TryGetValue(key, out var value) ? value : null,
                document.Name,
                document.Project.Name);
        }
        catch (Exception)
        {
            // A project without analyzer-config support still gets the default behaviour.
            return StyleOptions.Default;
        }
    }

    /// <summary>The parsing half, separate so it can be tested without a workspace.</summary>
    public static StyleOptions Parse(Func<string, string?> option, string? fileName = null, string? projectName = null) => new()
    {
        Braces = option("csharp_prefer_braces")?.Trim().ToLowerInvariant() switch
        {
            "true" => BracePreference.Always,
            "when_multiline" => BracePreference.WhenMultiline,
            _ => BracePreference.None,
        },
        ModifierOrder = (option("csharp_preferred_modifier_order") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(modifier => modifier.ToLowerInvariant())
            .ToList(),
        Namespaces = option("csharp_style_namespace_declarations")?.Trim().ToLowerInvariant() switch
        {
            "file_scoped" => NamespacePreference.FileScoped,
            "block_scoped" => NamespacePreference.BlockScoped,
            _ => NamespacePreference.None,
        },
        ForeachExplicitCast = option("dotnet_style_prefer_foreach_explicit_cast_in_source")?.Trim().ToLowerInvariant() switch
        {
            "always" => ForeachCastPreference.Always,
            "when_strongly_typed" => ForeachCastPreference.WhenStronglyTyped,
            _ => ForeachCastPreference.None,
        },
        Accessibility = option("dotnet_style_require_accessibility_modifiers")?.Trim().ToLowerInvariant() switch
        {
            "always" => AccessibilityPreference.Always,
            "for_non_interface_members" => AccessibilityPreference.ForNonInterfaceMembers,
            "omit_if_default" => AccessibilityPreference.OmitIfDefault,
            _ => AccessibilityPreference.None,
        },
        FileHeader = ResolveHeader(option("file_header_template"), fileName, projectName),
    };

    /// <summary>
    /// The header template with its placeholders filled in: <c>\n</c> separates the lines (that is how
    /// the option is written in an <c>.editorconfig</c>), and the tokens the IDE documents are
    /// substituted. A token this engine does not know is left as written, so a template is never silently
    /// altered.
    /// </summary>
    internal static string? ResolveHeader(string? template, string? fileName, string? projectName)
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

/// <summary>The C# rule set as the shared service sees it: the catalogue and its refusals.</summary>
public sealed class CSharpStyleRuleSet : IStyleRuleSet
{
    public static readonly CSharpStyleRuleSet Default = new();

    public IReadOnlyList<StyleRewrite> All => StyleRewriteCatalog.All;

    public IReadOnlyList<StyleRule> Supported => StyleRewriteCatalog.Supported;

    public IReadOnlyList<(string Id, string Reason)> NotRewritten => StyleRewriteCatalog.NotRewritten;

    public string SupportedIds => StyleRewriteCatalog.SupportedIds;
}
