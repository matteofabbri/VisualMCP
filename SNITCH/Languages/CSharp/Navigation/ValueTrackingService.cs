using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Navigation;

/// <summary>
/// Where a value comes from and where it goes: the definitions (writes) and the uses (reads) of the symbol
/// under a position, each with the member it happens in. It is the data behind "value tracking" — the IDE
/// jumps to a write from a read; the server lists them.
/// </summary>
public interface IValueTrackingService
{
    Task<object> TrackAsync(string filePath, int line, string? name);
}

public sealed class ValueTrackingService : IValueTrackingService
{
    private readonly IRoslynWorkspace _workspace;
    public ValueTrackingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> TrackAsync(string filePath, int line, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-loaded from the working directory." };

        var full = Path.GetFullPath(filePath);
        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), full, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{full} is not part of the loaded solution." };

        var text = await document.GetTextAsync();
        if (line > text.Lines.Count) return new { error = $"Line {line} is out of range ({text.Lines.Count} line(s))." };

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null) return new { error = "The document has no syntax or semantic model." };

        var span = text.Lines[line - 1].Span;
        var identifiers = root.DescendantNodes(span).OfType<IdentifierNameSyntax>()
            .Where(identifier => name is null || identifier.Identifier.ValueText.Equals(name, StringComparison.Ordinal))
            .ToList();

        if (identifiers.Count == 0)
            return new { error = $"No identifier{(name is null ? "" : $" named '{name}'")} on line {line}." };

        var symbol = identifiers
            .Select(identifier => model.GetSymbolInfo(identifier).Symbol)
            .FirstOrDefault(candidate => candidate is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol);

        if (symbol is null)
            return new { error = $"The identifier on line {line} does not bind to a local, parameter, field or property." };

        var definitions = new List<object>();
        var uses = new List<object>();

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution))
        {
            foreach (var location in reference.Locations.Where(location => location.Location.IsInSource))
            {
                var siteRoot = await location.Document.GetSyntaxRootAsync();
                var position = location.Location.SourceSpan.Start;
                var lineSpan = location.Location.GetLineSpan();

                var member = siteRoot?.FindToken(position).Parent?.AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>().FirstOrDefault();

                var entry = new
                {
                    file = location.Document.FilePath,
                    line = lineSpan.StartLinePosition.Line + 1,
                    member = member is null ? null : MemberName(member),
                    snippet = Snippet(siteRoot, position),
                };

                if (IsWrite(siteRoot, position)) definitions.Add(entry);
                else uses.Add(entry);
            }
        }

        // The declaration is a definition even when nothing writes it afterwards.
        var declaration = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (declaration is not null)
        {
            var declarationSpan = declaration.GetLineSpan();
            definitions.Insert(0, new
            {
                file = declaration.SourceTree?.FilePath,
                line = declarationSpan.StartLinePosition.Line + 1,
                member = (string?)null,
                snippet = Snippet(declaration.SourceTree?.GetRoot(), declaration.SourceSpan.Start),
                kind = "declaration",
            });
        }

        return new
        {
            symbol = symbol.ToDisplayString(),
            kind = symbol.Kind.ToString(),
            file = full,
            line,
            definitions = definitions,
            uses = uses,
            definitionCount = definitions.Count,
            useCount = uses.Count,
            note = "Definitions are the writes (assignments, initializers, out/ref arguments, and the declaration " +
                   "itself); uses are the reads. A write through a property setter appears on the property, not on " +
                   "the backing field.",
        };
    }

    /// <summary>
    /// Whether the reference is a write: an assignment target, an increment/decrement, or an <c>out</c>/
    /// <c>ref</c> argument. Roslyn's own read/write flag is internal, so the syntax decides.
    /// </summary>
    private static bool IsWrite(SyntaxNode? root, int position)
    {
        if (root is null) return false;

        for (var current = root.FindToken(position).Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case AssignmentExpressionSyntax assignment when assignment.Left.Span.Contains(position):
                    return true;
                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    return true;
                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return true;
                case ArgumentSyntax argument when argument.Span.Contains(position):
                    return argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ||
                           argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword);
                case StatementSyntax:
                case MemberDeclarationSyntax:
                    return false;
            }
        }

        return false;
    }

    private static string MemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        TypeDeclarationSyntax type => type.Identifier.ValueText,
        _ => member.Kind().ToString(),
    };

    private static string? Snippet(SyntaxNode? root, int position)
    {
        if (root is null) return null;
        var line = root.FindToken(position).GetLocation().GetLineSpan();
        return line.StartLinePosition.Line == line.EndLinePosition.Line
            ? root.SyntaxTree.GetText().Lines[line.StartLinePosition.Line].ToString().Trim()
            : null;
    }
}
