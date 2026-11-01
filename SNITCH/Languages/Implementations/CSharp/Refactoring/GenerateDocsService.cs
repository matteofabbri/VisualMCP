using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// ReSharper's "Generate XML documentation" (GhostDoc style): writes the <c>&lt;summary&gt;</c> and the
/// <c>&lt;param&gt;</c>/<c>&lt;typeparam&gt;</c>/<c>&lt;returns&gt;</c> entries a declaration is missing,
/// with a sentence derived from the name. It never touches a declaration that already has a doc
/// comment, and it only inserts comment lines — the code below them is not re-emitted.
/// <para>
/// C# writes them as <c>///</c> lines and Visual Basic as <c>'''</c> lines; the selection (which
/// declarations are public and undocumented) is read from the symbols, so both languages use it.
/// </para>
/// </summary>
public interface IGenerateDocsService
{
    Task<object> GenerateDocs(
        string filePath,
        string? symbolName,
        bool includeMembers,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed partial class GenerateDocsService : IGenerateDocsService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public GenerateDocsService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> GenerateDocs(
        string filePath,
        string? symbolName,
        bool includeMembers,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var document = FindDocument(solution, filePath);
        if (document is null) return new { error = $"{filePath} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read {Path.GetFileName(document.FilePath)}." };

        var declarations = Targets(root, model, symbolName, includeMembers, out var selectionError, cancellationToken);
        if (selectionError is not null) return selectionError;
        if (declarations.Count == 0)
            return new
            {
                tool = "generate_docs",
                @file = document.FilePath,
                count = 0,
                generated = Array.Empty<object>(),
                note = symbolName is null
                    ? "No undocumented public declaration in this file."
                    : $"'{symbolName}' already has documentation or declares nothing.",
            };

        var insertions = new List<(int Position, string Comment, string Symbol)>();
        var newline = text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = document.Project.Language == LanguageNames.VisualBasic ? "''' " : "/// ";

        foreach (var declaration in declarations)
        {
            var symbol = DeclaredSymbol(model, declaration, cancellationToken);
            if (symbol is null) continue;

            var line = text.Lines.GetLineFromPosition(declaration.SpanStart);
            var indentation = text.ToString(TextSpan.FromBounds(line.Start, declaration.SpanStart));
            if (indentation.Any(character => character is not (' ' or '\t'))) indentation = string.Empty;

            insertions.Add((line.Start, Comment(symbol, indentation, prefix, newline), symbol.ToDisplayString()));
        }

        if (insertions.Count == 0)
            return new { tool = "generate_docs", @file = document.FilePath, count = 0, generated = Array.Empty<object>() };

        // Splice bottom-up so the earlier positions stay valid.
        var source = text.ToString();
        foreach (var insertion in insertions.OrderByDescending(entry => entry.Position))
            source = source[..insertion.Position] + insertion.Comment + source[insertion.Position..];

        if (!dryRun)
        {
            _store?.RecordEdit("generate_docs", document.FilePath!, symbolName, text.ToString(), source);
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, source, cancellationToken);
        }

        return new
        {
            tool = "generate_docs",
            @file = document.FilePath,
            count = insertions.Count,
            generated = insertions
                .OrderBy(entry => entry.Position)
                .Select(entry => new { symbol = entry.Symbol, line = text.Lines.GetLineFromPosition(entry.Position).LineNumber + 1 })
                .ToList(),
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(text.ToString(), source),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>
    /// The declarations to document: one named symbol, or every public undocumented type (and, when
    /// <paramref name="includeMembers"/>, its public members) in the file.
    /// </summary>
    private static List<SyntaxNode> Targets(
        SyntaxNode root,
        SemanticModel model,
        string? symbolName,
        bool includeMembers,
        out object? error,
        CancellationToken cancellationToken)
    {
        error = null;
        var wanted = new List<SyntaxNode>();

        if (!string.IsNullOrWhiteSpace(symbolName))
        {
            var declaration = root.DescendantNodes()
                .Where(IsDocumentableDeclaration)
                .FirstOrDefault(candidate => NameOf(candidate) == symbolName);

            if (declaration is null)
            {
                error = new { error = $"No declaration named '{symbolName}' in this file." };
                return wanted;
            }

            wanted.Add(declaration);

            if (includeMembers && IsTypeDeclaration(declaration))
                wanted.AddRange(MemberDeclarations(declaration).Where(member => IsPublic(member, model, cancellationToken)));

            return wanted.Where(candidate => !HasDocs(candidate)).ToList();
        }

        foreach (var type in root.DescendantNodes().Where(IsTypeDeclaration))
        {
            if (model.GetDeclaredSymbol(type, cancellationToken) is not { DeclaredAccessibility: Accessibility.Public }) continue;

            if (!HasDocs(type)) wanted.Add(type);
            if (!includeMembers) continue;

            wanted.AddRange(MemberDeclarations(type)
                .Where(member => IsPublic(member, model, cancellationToken) && !HasDocs(member)));
        }

        return wanted;
    }

    /// <summary>Everything a doc comment can be attached to; the language decides which node kinds qualify.</summary>
    private static bool IsDocumentableDeclaration(SyntaxNode node) =>
        node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? IsVisualBasicDocumentable(node)
            : node is MemberDeclarationSyntax;

    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? IsVisualBasicTypeDeclaration(node)
            : node is TypeDeclarationSyntax;

    private static IEnumerable<SyntaxNode> MemberDeclarations(SyntaxNode type) =>
        type.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicMemberDeclarations(type)
            : ((TypeDeclarationSyntax)type).Members.Cast<SyntaxNode>();

    /// <summary>
    /// The public test is the symbol's accessibility, not a keyword: it is the same answer in both
    /// languages and it knows VB's defaults (a procedure is Public, a <c>Dim</c> field is Private).
    /// </summary>
    private static bool IsPublic(SyntaxNode member, SemanticModel model, CancellationToken cancellationToken) =>
        DeclaredSymbol(model, member, cancellationToken)?.DeclaredAccessibility == Accessibility.Public;

    private static bool HasDocs(SyntaxNode member)
    {
        var leading = member.GetLeadingTrivia().ToString();
        return leading.Contains("///", StringComparison.Ordinal)
               || leading.Contains("'''", StringComparison.Ordinal);
    }

    /// <summary>
    /// The symbol a declaration introduces. Visual Basic attaches a field's symbol to the declarator,
    /// not to the statement, so the first declarator is the one that answers.
    /// </summary>
    private static ISymbol? DeclaredSymbol(SemanticModel model, SyntaxNode declaration, CancellationToken cancellationToken) =>
        declaration.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicDeclaredSymbol(model, declaration, cancellationToken)
            : model.GetDeclaredSymbol(declaration, cancellationToken);

    private static string? NameOf(SyntaxNode member) =>
        member.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicNameOf(member)
            : member switch
            {
                BaseTypeDeclarationSyntax type      => type.Identifier.ValueText,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
                MethodDeclarationSyntax method      => method.Identifier.ValueText,
                PropertyDeclarationSyntax property  => property.Identifier.ValueText,
                EventDeclarationSyntax @event       => @event.Identifier.ValueText,
                FieldDeclarationSyntax field        => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
                _                                   => null,
            };

    // ── the comment ──────────────────────────────────────────────────────────

    private sealed record CommentText(string Summary, IReadOnlyList<string> TypeParameters,
        IReadOnlyList<string> Parameters, bool Returns);

    private static string Comment(ISymbol symbol, string indentation, string prefix, string newline)
    {
        var comment = Describe(symbol);
        var builder = new StringBuilder();
        void Line(string text) => builder.Append(indentation).Append(prefix).Append(text).Append(newline);

        Line("<summary>");
        Line(Escape(comment.Summary));
        Line("</summary>");

        foreach (var parameter in comment.TypeParameters)
            Line($"<typeparam name=\"{Escape(parameter)}\">{Escape(Article(parameter))}</typeparam>");

        foreach (var parameter in comment.Parameters)
            Line($"<param name=\"{Escape(parameter)}\">{Escape("The " + Words(parameter).ToLowerInvariant() + ".")}</param>");

        if (comment.Returns) Line("<returns>The result.</returns>");

        return builder.ToString();
    }

    private static CommentText Describe(ISymbol symbol)
    {
        var name = symbol.Name;
        var words = Words(name);

        switch (symbol)
        {
            case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                return new CommentText(
                    $"Initializes a new instance of the {constructor.ContainingType.Name} class.",
                    TypeParameterNames(constructor),
                    constructor.Parameters.Select(parameter => parameter.Name).ToList(),
                    Returns: false);

            case IMethodSymbol method:
                return new CommentText(
                    ThirdPerson(Lower(words)) + ".",
                    TypeParameterNames(method),
                    method.Parameters.Select(parameter => parameter.Name).ToList(),
                    Returns: !method.ReturnsVoid);

            case IPropertySymbol property:
                return new CommentText(
                    $"Gets or sets {Lower(words)}.",
                    Array.Empty<string>(),
                    property.Parameters.Select(parameter => parameter.Name).ToList(),
                    Returns: false);

            case IFieldSymbol field:
                return new CommentText($"The {Lower(words)}.", Array.Empty<string>(), Array.Empty<string>(), false);

            case IEventSymbol @event:
                return new CommentText($"Raised when {@event.Name}.", Array.Empty<string>(), Array.Empty<string>(), false);

            case INamedTypeSymbol type:
                return new CommentText($"Represents {Lower(words)}.", TypeParameterNames(type),
                    Array.Empty<string>(), false);

            default:
                return new CommentText($"The {Lower(words)}.", Array.Empty<string>(), Array.Empty<string>(), false);
        }
    }

    private static IReadOnlyList<string> TypeParameterNames(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.TypeParameters.Select(parameter => parameter.Name).ToList(),
        INamedTypeSymbol type => type.TypeParameters.Select(parameter => parameter.Name).ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>"CalculateTotal" → "Calculate total"; an all-caps run (an acronym) is kept as it is.</summary>
    private static string Words(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var builder = new StringBuilder(name.Length + 8);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (character is '_' or '-')
            {
                builder.Append(' ');
                continue;
            }

            if (index > 0 && char.IsUpper(character) &&
                (char.IsLower(name[index - 1]) ||
                 (index + 1 < name.Length && char.IsLower(name[index + 1]))))
                builder.Append(' ');

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// "CalculateTotal" → "calculate total": only the first word keeps its capital, while an all-caps
    /// run (an acronym such as <c>ID</c>) is left alone.
    /// </summary>
    private static string Lower(string words)
    {
        var parts = words.Split(' ');
        for (var index = 1; index < parts.Length; index++)
        {
            if (parts[index].Length == 0) continue;
            if (parts[index].Length > 1 && parts[index].All(char.IsUpper)) continue;
            parts[index] = char.ToLowerInvariant(parts[index][0]) + parts[index][1..];
        }

        var joined = string.Join(' ', parts);
        return joined.Length == 0 ? joined : char.ToLowerInvariant(joined[0]) + joined[1..];
    }

    /// <summary>"calculate total" → "Calculates total"; a verb already in the third person is left alone.</summary>
    private static string ThirdPerson(string text)
    {
        var words = text.Split(' ', 2);
        var verb = words[0];
        var result = text;

        if (!verb.EndsWith('s'))
        {
            if (verb.EndsWith('y') && verb.Length > 1 && !"aeiouAEIOU".Contains(verb[^2]))
                result = verb[..^1] + "ies" + (words.Length > 1 ? " " + words[1] : string.Empty);
            else
                result = verb + "s" + (words.Length > 1 ? " " + words[1] : string.Empty);
        }

        return result.Length == 0 ? result : char.ToUpperInvariant(result[0]) + result[1..];
    }

    private static string Article(string word) => $"The {Words(word).ToLowerInvariant()}.";

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static Document? FindDocument(Solution solution, string filePath)
    {
        var path = Path.GetFullPath(filePath);
        return solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path,
                                                       StringComparison.OrdinalIgnoreCase));
    }
}
