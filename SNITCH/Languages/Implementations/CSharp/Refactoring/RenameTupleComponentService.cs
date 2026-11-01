using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Rename tuple component": renames one element of a named tuple in its type spelling and in
/// every <c>.oldName</c> access whose receiver is that tuple.
/// </summary>
public interface IRenameTupleComponentService
{
    Task<object> RenameTupleComponent(
        string filePath,
        int line,
        string oldName,
        string newName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class RenameTupleComponentService : IRenameTupleComponentService
{
    private readonly IRoslynWorkspace _workspace;

    public RenameTupleComponentService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RenameTupleComponent(
        string filePath,
        int line,
        string oldName,
        string newName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };
        if (string.IsNullOrWhiteSpace(oldName)) return new { error = "The current element name is required." };
        if (string.IsNullOrWhiteSpace(newName) || !SyntaxFacts.IsValidIdentifier(newName))
            return new { error = $"'{newName}' is not a valid identifier." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var declarationDocument = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (declarationDocument is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await declarationDocument.GetSyntaxRootAsync(cancellationToken);
        var model = await declarationDocument.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var tupleType = root.DescendantNodes().OfType<TupleTypeSyntax>()
            .Where(candidate => candidate.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .Where(candidate => candidate.Elements.Any(element => element.Identifier.ValueText == oldName))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (tupleType is null)
            return new { error = $"No tuple type with an element named '{oldName}' starts on line {line}." };

        if (model.GetTypeInfo(tupleType, cancellationToken).Type is not INamedTypeSymbol { IsTupleType: true } tuple)
            return new { error = "The tuple type did not resolve." };

        var element = tuple.TupleElements.FirstOrDefault(candidate => candidate.Name == oldName);
        if (element is null) return new { error = $"'{oldName}' is not a tuple element of that type." };
        if (tuple.TupleElements.Any(candidate => candidate.Name == newName))
            return new { error = $"The tuple already has an element named '{newName}'." };

        var elementTypes = tuple.TupleElements.Select(candidate => candidate.Type).ToList();
        var elementIndex = tuple.TupleElements.IndexOf(element);

        // ── Collect: every .oldName access on this tuple + every type spelling of the same shape ──
        var edits = new List<(Document Document, string NewText, int Accesses, int TypeSpellings, string Preview)>();

        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            var documentRoot = await document.GetSyntaxRootAsync(cancellationToken);
            var documentModel = await document.GetSemanticModelAsync(cancellationToken);
            if (documentRoot is null || documentModel is null) continue;

            var accesses = documentRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(access => access.Name.Identifier.ValueText == oldName)
                .Where(access => IsSameTuple(documentModel.GetTypeInfo(access.Expression, cancellationToken).Type, elementTypes))
                .ToList();

            var spellings = documentRoot.DescendantNodes().OfType<TupleTypeSyntax>()
                .Where(candidate => candidate.Elements.Count == elementTypes.Count)
                .Where(candidate => candidate.Elements[elementIndex].Identifier.ValueText == oldName)
                .Where(candidate => IsSameTuple(documentModel.GetTypeInfo(candidate, cancellationToken).Type, elementTypes))
                .ToList();

            if (accesses.Count == 0 && spellings.Count == 0) continue;

            // Both rewrites change text length, so a span captured before one of them is stale after
            // it: annotate every target first, then replace by annotation.
            var accessAnnotation = new SyntaxAnnotation("tuple-access");
            var spellingAnnotation = new SyntaxAnnotation("tuple-spelling");

            var annotated = documentRoot.ReplaceNodes(
                accesses.Cast<SyntaxNode>().Concat(spellings),
                (original, _) => original.WithAdditionalAnnotations(
                    original is MemberAccessExpressionSyntax ? accessAnnotation : spellingAnnotation));

            var renamedAccesses = annotated.ReplaceNodes(
                annotated.GetAnnotatedNodes(accessAnnotation).OfType<MemberAccessExpressionSyntax>(),
                (original, _) => original.WithName(IdentifierName(newName).WithTriviaFrom(original.Name)));

            var newRoot = renamedAccesses.ReplaceNodes(
                renamedAccesses.GetAnnotatedNodes(spellingAnnotation).OfType<TupleTypeSyntax>(),
                (original, _) => original.WithElements(original.Elements.Replace(
                    original.Elements[elementIndex],
                    original.Elements[elementIndex].WithIdentifier(
                        Identifier(newName).WithTriviaFrom(original.Elements[elementIndex].Identifier)))));

            var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((document, newText, accesses.Count, spellings.Count,
                RefactoringFileWriter.Diff(oldText, newText)));
        }

        if (edits.Count == 0) return new { error = $"No occurrence of '{oldName}' to rename." };

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "rename_tuple_component",
            tuple = tuple.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            from = oldName,
            to = newName,
            file = declarationDocument.FilePath,
            line,
            dryRun,
            applied = !dryRun,
            accessNamesRenamed = edits.Sum(edit => edit.Accesses),
            typeSpellingsRenamed = edits.Sum(edit => edit.TypeSpellings),
            filesChanged = edits.Count,
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                accesses = edit.Accesses,
                typeSpellings = edit.TypeSpellings,
                preview = edit.Preview,
            }).ToList(),
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files."
                : "Files written. Tuple element names are not part of the runtime type: overloads/casts that used the old name are still compatible.",
        };
    }

    /// <summary>A tuple type with the same element types, whatever its element names are.</summary>
    private static bool IsSameTuple(ITypeSymbol? candidate, IReadOnlyList<ITypeSymbol> elementTypes)
    {
        if (candidate is not INamedTypeSymbol { IsTupleType: true } named) return false;
        var elements = named.TupleElements;
        if (elements.Length != elementTypes.Count) return false;

        for (var index = 0; index < elements.Length; index++)
        {
            if (!SymbolEqualityComparer.Default.Equals(elements[index].Type, elementTypes[index])) return false;
        }

        return true;
    }
}
