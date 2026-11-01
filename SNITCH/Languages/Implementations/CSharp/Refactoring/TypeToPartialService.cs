using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>Rider's "Make partial" (Type2Partial): marks every declaration of a type <c>partial</c>, so it can be split.</summary>
public interface ITypeToPartialService
{
    Task<object> TypeToPartial(
        string filePath,
        string typeName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class TypeToPartialService : ITypeToPartialService
{
    private readonly IRoslynWorkspace _workspace;

    public TypeToPartialService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> TypeToPartial(
        string filePath,
        string typeName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "A type name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var documentsByPath = solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => document.FilePath is not null)
            .GroupBy(document => Path.GetFullPath(document.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (!documentsByPath.TryGetValue(path, out var document))
            return new { error = $"{path} is not a document of the loaded solution." };

        if (document.Project.Language == LanguageNames.VisualBasic)
            return await Snitch.Languages.VB.Refactoring.VbRefactoringSupport.TypeToPartialAsync(
                _workspace, document, typeName, containingType, dryRun, cancellationToken);

        // See PropertyToAutoService: a write leaves the in-memory solution stale, and rewriting from
        // stale text would undo whatever another refactoring just wrote.
        var diskText = await File.ReadAllTextAsync(path, cancellationToken);
        var modelledText = (await document.GetTextAsync(cancellationToken)).ToString();
        if (!string.Equals(diskText, modelledText, StringComparison.Ordinal))
            return new
            {
                error = $"{Path.GetFileName(path)} changed on disk after the solution was loaded; the in-memory model is stale. " +
                        "Call load_solution, then retry.",
            };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var candidates = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(declaration => declaration.Identifier.ValueText == typeName)
            .Where(declaration => containingType is null ||
                                  string.Equals(model.GetDeclaredSymbol(declaration, cancellationToken)?.ContainingType?.Name,
                                                containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            var unsupported = root.DescendantNodes().FirstOrDefault(node => node switch
            {
                EnumDeclarationSyntax enumDeclaration => enumDeclaration.Identifier.ValueText == typeName,
                DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Identifier.ValueText == typeName,
                _ => false,
            });

            return new
            {
                error = $"No class, struct, interface or record named '{typeName}' in {Path.GetFileName(path)}" +
                        (unsupported is not null ? "; enums and delegates cannot be partial." : "."),
            };
        }

        if (candidates.Count > 1)
            return new
            {
                error = $"Several types named '{typeName}' are declared in the file; pass containingType.",
                types = candidates.Select(candidatesDeclaration => model.GetDeclaredSymbol(candidatesDeclaration, cancellationToken)?.ToDisplayString()).ToList(),
            };

        if (model.GetDeclaredSymbol(candidates[0], cancellationToken) is not INamedTypeSymbol symbol)
            return new { error = $"'{typeName}' did not resolve to a type symbol." };

        // Every declaration of the type must travel together, otherwise the C# compiler rejects the
        // mix of partial and non-partial declarations.
        var pending = new Dictionary<DocumentId, (Document Document, List<TypeDeclarationSyntax> Nodes)>();
        var alreadyPartial = 0;
        var inaccessible = new List<string>();

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var tree = reference.SyntaxTree;
            var declarationPath = tree.FilePath is null ? null : Path.GetFullPath(tree.FilePath);
            if (declarationPath is null || !documentsByPath.TryGetValue(declarationPath, out var declarationDocument))
            {
                inaccessible.Add(declarationPath ?? "(in memory)");
                continue;
            }

            if (await reference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax declaration)
            {
                inaccessible.Add(declarationPath);
                continue;
            }

            if (declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                alreadyPartial++;
                continue;
            }

            if (!pending.TryGetValue(declarationDocument.Id, out var bucket))
            {
                bucket = (declarationDocument, new List<TypeDeclarationSyntax>());
                pending[declarationDocument.Id] = bucket;
            }

            bucket.Nodes.Add(declaration);
        }

        if (pending.Count == 0)
            return new
            {
                @refactoring = "type_to_partial",
                type = symbol.ToDisplayString(),
                dryRun,
                applied = false,
                alreadyPartial,
                note = alreadyPartial > 0 ? "Every declaration is already partial; nothing to do." : "No declaration could be updated.",
                notInSolution = inaccessible,
            };

        var results = new List<object>();

        foreach (var (documentId, bucket) in pending)
        {
            var currentRoot = await bucket.Document.GetSyntaxRootAsync(cancellationToken);
            if (currentRoot is null) continue;

            var newRoot = currentRoot.ReplaceNodes(
                bucket.Nodes,
                (original, _) => original.AddModifiers(Token(SyntaxKind.PartialKeyword).WithTrailingTrivia(Space)));

            var oldText = (await bucket.Document.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();

            if (!dryRun && bucket.Document.FilePath is not null)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, bucket.Document, newText, cancellationToken);

            results.Add(new
            {
                file = bucket.Document.FilePath,
                declarations = bucket.Nodes.Count,
                preview = RefactoringFileWriter.Diff(oldText, newText),
            });
        }

        return new
        {
            @refactoring = "type_to_partial",
            type = symbol.ToDisplayString(),
            dryRun,
            applied = !dryRun,
            declarationsUpdated = pending.Sum(bucket => bucket.Value.Nodes.Count),
            filesChanged = results.Count,
            alreadyPartial,
            notInSolution = inaccessible,
            files = results,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }
}
