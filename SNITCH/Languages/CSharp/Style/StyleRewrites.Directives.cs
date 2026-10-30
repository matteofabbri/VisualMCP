using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0240 / IDE0241: a <c>#nullable</c> directive that does not change the state — <c>enable</c> when
/// the project already enables it, <c>disable</c> when it already disables it, <c>restore</c> when the
/// project default is already in effect. This is the engine's first trivia-level rewrite: the fix edits
/// the directive itself, not a syntax node, and it walks the file keeping the running state so a second
/// directive is judged against what the first one did.
/// </summary>
public sealed class RedundantNullableDirectiveRewrite : CSharpStyleRewrite
{
    private static readonly StyleRule Redundant = new(
        "IDE0240", "Remove redundant nullable directive",
        "#nullable enable where the project already enables it");

    private static readonly StyleRule Unnecessary = new(
        "IDE0241", "Remove unnecessary nullable directive",
        "#nullable restore that restores the state already in effect");

    public override StyleRule Rule => Redundant;

    public override IReadOnlyList<StyleRule> Rules => [Redundant, Unnecessary];

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (node is not CompilationUnitSyntax unit) return null;
        if (context.Model.Compilation.Options is not CSharpCompilationOptions options) return null;

        var projectState = options.NullableContextOptions;
        var state = projectState;
        var removable = new List<SyntaxTrivia>();
        var onlyRestores = true;

        foreach (var trivia in unit.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.NullableDirectiveTrivia)) continue;
            if (trivia.GetStructure() is not NullableDirectiveTriviaSyntax directive) continue;
            if (!directive.IsActive) continue;

            switch (directive.SettingToken.Text)
            {
                case "enable":
                    if (state == NullableContextOptions.Enable) removable.Add(trivia);
                    else state = NullableContextOptions.Enable;
                    onlyRestores = false;
                    break;

                case "disable":
                    if (state == NullableContextOptions.Disable) removable.Add(trivia);
                    else state = NullableContextOptions.Disable;
                    onlyRestores = false;
                    break;

                case "restore":
                    if (state == projectState) removable.Add(trivia);
                    else state = projectState;
                    break;

                // `annotations` and `warnings` are a different axis: left alone.
                default:
                    break;
            }
        }

        if (removable.Count == 0) return null;

        var drop = removable.ToHashSet();
        var owners = unit.DescendantTokens()
            .Where(token => token.LeadingTrivia.Any(drop.Contains) || token.TrailingTrivia.Any(drop.Contains))
            .ToList();

        if (owners.Count == 0) return null;

        var replacement = unit.ReplaceTokens(owners, (original, _) =>
            original
                .WithLeadingTrivia(original.LeadingTrivia.Where(trivia => !drop.Contains(trivia)))
                .WithTrailingTrivia(original.TrailingTrivia.Where(trivia => !drop.Contains(trivia))));

        return Match(onlyRestores ? Unnecessary : Redundant, node, replacement);
    }
}
