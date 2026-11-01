using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Push members down", restricted to the case that is a pure relocation: the base type has
/// exactly one derived type in source and the member is used only inside that derived type. Anything
/// else would change what callers through the base type can do, so it is refused.
/// </summary>
public interface IPushMembersDownService
{
    Task<object> PushMembersDown(
        string filePath,
        string memberName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class PushMembersDownService : IPushMembersDownService
{
    private readonly IRoslynWorkspace _workspace;

    public PushMembersDownService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> PushMembersDown(
        string filePath,
        string memberName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(memberName)) return new { error = "The member name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var declaration = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(member => member is MethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax
                                      && NameOf(member) == memberName
                                      && member.Parent is TypeDeclarationSyntax parent &&
                                      (containingType is null ||
                                       string.Equals(parent.Identifier.ValueText, containingType, StringComparison.Ordinal)));
        if (declaration is null)
            return new { error = $"No method, property or field '{memberName}' in {Path.GetFileName(path)}." };

        var baseType = (TypeDeclarationSyntax)declaration.Parent!;
        if (model.GetDeclaredSymbol(baseType, cancellationToken) is not INamedTypeSymbol baseSymbol)
            return new { error = $"'{baseType.Identifier.ValueText}' did not resolve." };

        var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
        if (symbol is null) return new { error = $"'{memberName}' did not resolve to a symbol." };

        if (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride)
            return new { error = $"'{memberName}' is abstract/virtual/override: pushing it down would break the hierarchy contract." };

        // ── The base must have exactly one derived type, and only it may use the member ────
        var derived = (await SymbolFinder.FindDerivedClassesAsync(
                baseSymbol, solution, projects: null, transitive: false, cancellationToken: cancellationToken))
            .Where(type => type.Locations.Any(location => location.IsInSource))
            .ToList();

        if (derived.Count != 1)
            return new
            {
                error = derived.Count == 0
                    ? $"'{baseSymbol.Name}' has no derived type in source; nothing to push down to."
                    : $"'{baseSymbol.Name}' has {derived.Count} derived types; pushing a member down to all of them is not a relocation.",
                derivedTypes = derived.Select(type => type.ToDisplayString()).ToList(),
            };

        var target = derived[0];

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var enclosing = referenceRoot?.FindNode(location.Location.SourceSpan)
                    .AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var enclosingSymbol = enclosing is null ? null : (await location.Document.GetSemanticModelAsync(cancellationToken))?
                    .GetDeclaredSymbol(enclosing, cancellationToken);

                if (!SymbolEqualityComparer.Default.Equals(enclosingSymbol, target))
                    return new
                    {
                        error = $"'{memberName}' is used outside '{target.Name}' " +
                                $"({Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}); " +
                                "pushing it down would break that use.",
                    };
            }
        }

        // ── Target document: insert the member into the derived type ─────────────────────
        var targetTree = target.DeclaringSyntaxReferences.Select(reference => reference.SyntaxTree).FirstOrDefault();
        var targetDocumentId = targetTree is null ? null : solution.GetDocumentId(targetTree);
        var targetDocument = targetDocumentId is null ? null : solution.GetDocument(targetDocumentId);
        if (targetDocument is null) return new { error = $"The file of '{target.Name}' is not part of the solution." };

        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (targetRoot is null) return new { error = $"Could not read '{target.Name}'." };

        var targetDeclaration = targetRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == target.Name);
        if (targetDeclaration is null) return new { error = $"Could not locate the declaration of '{target.Name}'." };

        var indentation = IndentationOf(targetDeclaration);
        var moved = ParseMemberDeclaration(Reindent(declaration.NormalizeWhitespace().ToFullString(), indentation));
        if (moved is null) return new { error = $"'{memberName}' could not be re-parsed." };

        moved = moved
            .WithLeadingTrivia(ElasticCarriageReturnLineFeed, Whitespace(indentation))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        var sameDocument = targetDocument.Id == document.Id;

        var newDerived = targetDeclaration.WithMembers(targetDeclaration.Members.Add(moved));
        var targetNewRoot = sameDocument
            ? targetRoot.ReplaceNode(targetDeclaration, newDerived)
            : targetRoot.ReplaceNode(targetDeclaration, newDerived);

        // Remove from the base — in the same tree when both live in one file.
        var declarationToRemove = sameDocument
            ? targetNewRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .First(candidate => candidate.Identifier.ValueText == baseType.Identifier.ValueText)
            : null;

        var baseNewRoot = sameDocument
            ? targetNewRoot.ReplaceNode(declarationToRemove!,
                declarationToRemove!.WithMembers(declarationToRemove.Members
                    .Remove(declarationToRemove.Members.First(member => NameOf(member) == memberName))))
            : root.ReplaceNode(baseType, baseType.WithMembers(baseType.Members.Remove(declaration)));

        // ── Gate: the project must still compile ──────────────────────────────────────────
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation is null) return new { error = "The project produced no compilation." };

        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var projected = document.Project.Solution
            .WithDocumentText(document.Id, SourceText.From(baseNewRoot.ToFullString()));
        if (!sameDocument)
            projected = projected.WithDocumentText(targetDocument.Id, SourceText.From(targetNewRoot.ToFullString()));

        var afterDiagnostics = (await projected.GetProject(document.Project.Id)!.GetCompilationAsync(cancellationToken))!
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: pushing '{memberName}' down would add {added} compiler error(s) ({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, baseNewRoot.ToFullString(), cancellationToken);
            if (!sameDocument)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, targetDocument, targetNewRoot.ToFullString(), cancellationToken);
        }

        return new
        {
            @refactoring = "push_members_down",
            member = memberName,
            from = baseSymbol.ToDisplayString(),
            to = target.ToDisplayString(),
            filesChanged = sameDocument ? 1 : 2,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 => field.Declaration.Variables[0].Identifier.ValueText,
        _ => null,
    };

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }

    private static string Reindent(string text, string indentation)
    {
        if (indentation.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) =>
            index == 0 || line.Length == 0 ? line : indentation + line));
    }
}
