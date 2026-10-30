using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// IDE0079: a <c>[SuppressMessage]</c> whose compiler diagnostic never fires on the member it decorates.
/// <para>
/// Only compiler ids (<c>CS####</c>) are judged. An analyzer's id cannot be checked without running that
/// analyzer, and a suppression that looks unnecessary because nothing loaded the analyzer is exactly the
/// false positive this rule must not produce — the attribute stays.
/// </para>
/// </summary>
public sealed class UnnecessarySuppressionAttributeRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0079", "Remove unnecessary suppression",
        "[SuppressMessage(\"Category\", \"CS0168\")] on a member whose CS0168 never fires");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not AttributeListSyntax list) return null;
        if (list.Parent is not MemberDeclarationSyntax member) return null;

        var attribute = list.Attributes.FirstOrDefault(candidate => Suppresses(candidate, model, cancellationToken));
        if (attribute is null) return null;

        if (DiagnosticId(attribute) is not { } id || !id.StartsWith("CS", StringComparison.Ordinal)) return null;

        // The compiler honours [SuppressMessage] itself, so the ordinary diagnostic list is already
        // filtered by it: asking whether a suppression is necessary needs the suppressed ones too.
        // WithReportSuppressedDiagnostics makes them appear, flagged; a fire in the member's span means
        // the attribute is doing its job.
        var compilation = model.Compilation.WithOptions(
            model.Compilation.Options.WithReportSuppressedDiagnostics(true));

        var span = member.Span;
        var fires = compilation.GetDiagnostics(cancellationToken).Any(diagnostic =>
            diagnostic.Id == id
            && diagnostic.Severity != DiagnosticSeverity.Hidden
            && span.Contains(diagnostic.Location.SourceSpan));

        if (fires) return null;

        return list.Attributes.Count == 1
            ? Removal(list)
            : Match(node, list.WithAttributes(list.Attributes.Remove(attribute)));
    }

    private static bool Suppresses(AttributeSyntax attribute, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetSymbolInfo(attribute, cancellationToken).Symbol
            is IMethodSymbol { ContainingType.Name: "SuppressMessageAttribute" };

    /// <summary>The second constructor argument: the diagnostic id the attribute names.</summary>
    private static string? DiagnosticId(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Count >= 2
        && attribute.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax literal
        && literal.Token.Value is string id
            ? id
            : null;
}
