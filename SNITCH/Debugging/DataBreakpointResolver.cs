using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Debugging;

/// <summary>
/// Turns "break when this member is written" into places a line breakpoint can go. The debug adapter
/// has no data-breakpoint request, so the write sites are the data: Roslyn resolves the member named
/// on a source line and classifies every use in the solution as a write (assignment, compound
/// assignment, ++/--, ref/out argument) or a read. A write from code the solution does not contain is
/// outside what this can see, and the tool says so.
/// </summary>
public static class DataBreakpointResolver
{
    /// <summary>
    /// The member named on <paramref name="file"/>:<paramref name="line"/> and every place the
    /// solution's source writes it, or an error that says why there is none.
    /// </summary>
    public static async Task<(DataBreakpoint? Breakpoint, string? Error)> ResolveAsync(
        Solution solution, string file, int line, string member)
    {
        var document = solution.Projects
            .SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null && DebugPath.Equal(candidate.FilePath, file));
        if (document is null)
            return (null, $"'{file}' is not a document of the loaded solution, so Roslyn cannot resolve the member.");

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null) return (null, $"'{file}' has no syntax tree to read.");

        var symbol = FindMemberSymbol(root, model, line, member);
        if (symbol is null)
            return (null, $"'{member}' is neither declared nor used on line {line} of {Path.GetFileName(file)}.");
        if (symbol is not (IFieldSymbol or IPropertySymbol))
            return (null, $"'{member}' is a {symbol.Kind}: a data breakpoint needs a field or a property.");

        var sites = new List<DataBreakpointSite>();

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution))
        {
            foreach (var location in reference.Locations)
            {
                if (!location.Location.IsInSource) continue;

                var referring = solution.GetDocument(location.Document.Id);
                if (referring?.FilePath is null) continue;

                var referringRoot = await referring.GetSyntaxRootAsync();
                if (referringRoot is null) continue;

                var node = referringRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var kind = ClassifyWrite(node);
                if (kind is null) continue;

                sites.Add(new DataBreakpointSite(referring.FilePath,
                    location.Location.GetLineSpan().StartLinePosition.Line + 1, kind));
            }
        }

        foreach (var declaration in symbol.DeclaringSyntaxReferences)
        {
            var declarationNode = await declaration.GetSyntaxAsync();
            if (!HasInitializer(declarationNode)) continue;

            sites.Add(new DataBreakpointSite(declaration.SyntaxTree.FilePath,
                declaration.SyntaxTree.GetLineSpan(declarationNode.Span).StartLinePosition.Line + 1, "initializer"));
        }

        var distinct = sites
            .GroupBy(site => (File: site.File.ToUpperInvariant(), site.Line))
            .Select(group => new DataBreakpointSite(group.First().File, group.Key.Line,
                string.Join("+", group.Select(site => site.Kind).Distinct(StringComparer.Ordinal))))
            .OrderBy(site => site.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(site => site.Line)
            .ToArray();

        if (distinct.Length == 0)
            return (null, $"No write site for '{member}' was found in the solution's source: it is only read.");

        return (new DataBreakpoint(member, symbol.ToDisplayString(), distinct), null);
    }

    /// <summary>The field or property symbol named on one line: a declaration or a use.</summary>
    private static ISymbol? FindMemberSymbol(SyntaxNode root, SemanticModel model, int line, string member)
    {
        var text = root.SyntaxTree.GetText();
        if (line < 1 || line > text.Lines.Count) return null;

        TextSpan span = text.Lines[line - 1].Span;
        foreach (var token in root.DescendantTokens(span))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;
            if (!string.Equals(token.ValueText, member, StringComparison.Ordinal)) continue;

            var node = token.Parent;
            if (node is null) continue;

            var declared = model.GetDeclaredSymbol(node);
            if (declared is not null) return declared;

            var info = model.GetSymbolInfo(node);
            if (info.Symbol is not null) return info.Symbol;
        }

        return null;
    }

    /// <summary>How a use writes the member, or null when it only reads it.</summary>
    private static string? ClassifyWrite(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AssignmentExpressionSyntax assignment:
                    if (!assignment.Left.Span.Contains(node.Span)) return null;
                    return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "assignment" : "compound-assignment";

                case PrefixUnaryExpressionSyntax prefix when
                    prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    return "increment";

                case PostfixUnaryExpressionSyntax postfix when
                    postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return "increment";

                case ArgumentSyntax argument when argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword):
                    return "ref-argument";

                case ArgumentSyntax argument when argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword):
                    return "out-argument";

                case StatementSyntax:
                case MemberDeclarationSyntax:
                    return null;
            }
        }

        return null;
    }

    private static bool HasInitializer(SyntaxNode declaration) => declaration switch
    {
        VariableDeclaratorSyntax declarator => declarator.Initializer is not null,
        PropertyDeclarationSyntax property => property.Initializer is not null,
        _ => false,
    };
}
