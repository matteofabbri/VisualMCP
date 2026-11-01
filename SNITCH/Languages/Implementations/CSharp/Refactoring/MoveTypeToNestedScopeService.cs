using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move type into another type": a namespace-level type becomes a nested type of another type
/// in the same project, and every reference outside the two declarations becomes <c>Target.Source</c>.
/// </summary>
/// <remarks>
/// The declaration moves between files, so the operation is gated on the whole solution: a reference in
/// another project still has to find the type at its new metadata name, and an accessibility that was
/// fine at namespace level may not be fine nested inside a type.
/// </remarks>
public interface IMoveTypeToNestedScopeService
{
    Task<object> MoveTypeToNestedScope(
        string filePath,
        string typeName,
        string targetTypeName,
        string? targetFilePath = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

public sealed class MoveTypeToNestedScopeService : IMoveTypeToNestedScopeService
{
    private readonly IRoslynWorkspace _workspace;

    public MoveTypeToNestedScopeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveTypeToNestedScope(
        string filePath,
        string typeName,
        string targetTypeName,
        string? targetFilePath = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type to move is required." };
        if (string.IsNullOrWhiteSpace(targetTypeName)) return new { error = "The target type is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var sourceDocument = FindDocument(solution, filePath);
        if (sourceDocument is null) return new { error = $"{Path.GetFullPath(filePath)} is not a document of the loaded solution." };

        var sourceRoot = await sourceDocument.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var sourceModel = await sourceDocument.GetSemanticModelAsync(cancellationToken);
        if (sourceRoot is null || sourceModel is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(filePath)}." };

        var declarations = sourceRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText.Equals(typeName, StringComparison.Ordinal))
            .ToList();

        if (declarations.Count == 0) return new { error = $"No type '{typeName}' in {Path.GetFileName(filePath)}." };
        if (declarations.Count > 1) return new { error = $"Several types named '{typeName}' in {Path.GetFileName(filePath)}." };

        var source = declarations[0];
        if (source.Parent is not (CompilationUnitSyntax or BaseNamespaceDeclarationSyntax))
            return new
            {
                error = $"'{typeName}' is already nested. Use move_type_to_outer_scope to pull it out of its container first.",
            };

        if (sourceModel.GetDeclaredSymbol(source, cancellationToken) is not INamedTypeSymbol sourceSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        // ── The target: any type of the same project that can declare members ────────────
        var targets = new List<(Document Document, BaseTypeDeclarationSyntax Node, INamedTypeSymbol Symbol)>();
        foreach (var document in sourceDocument.Project.Documents)
        {
            if (targetFilePath is not null && document.FilePath is not null &&
                !string.Equals(Path.GetFullPath(document.FilePath), Path.GetFullPath(targetFilePath), StringComparison.OrdinalIgnoreCase))
                continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null) continue;

            foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!node.Identifier.ValueText.Equals(targetTypeName, StringComparison.OrdinalIgnoreCase)) continue;
                if (node is EnumDeclarationSyntax) continue;
                if (node == source) continue;

                // Spans are only comparable inside one tree: another document's offsets mean nothing here.
                if (document.Id == sourceDocument.Id &&
                    (node.Span.Contains(source.Span) || source.Span.Contains(node.Span))) continue;
                if (model.GetDeclaredSymbol(node, cancellationToken) is not INamedTypeSymbol symbol) continue;

                targets.Add((document, node, symbol));
            }
        }

        if (targets.Count == 0)
            return new { error = $"No type '{targetTypeName}' that can hold '{typeName}' in project '{sourceDocument.Project.Name}'." };
        if (targets.Count > 1)
            return new
            {
                error = $"Several types named '{targetTypeName}'; pass targetFilePath.",
                candidates = targets.Select(target => target.Document.FilePath).ToList(),
            };

        var (targetDocument, targetNode, targetSymbol) = targets[0];
        if (targetDocument.Project.Id != sourceDocument.Project.Id)
            return new { error = "The target type is in another project: moving the declaration would change its assembly." };

        // ── References: everything outside the two declarations goes through the target ──
        var annotation = new SyntaxAnnotation("move_type_to_nested");
        var annotatedSourceRoot = sourceRoot.ReplaceNode(source, source.WithAdditionalAnnotations(annotation));

        var targetAnnotation = new SyntaxAnnotation("move_type_to_nested_target");
        if (targetDocument.Id == sourceDocument.Id)
        {
            var expandTarget = annotatedSourceRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .First(node => node.SpanStart == targetNode.SpanStart && node.RawKind == targetNode.RawKind);
            annotatedSourceRoot = annotatedSourceRoot.ReplaceNode(
                expandTarget, expandTarget.WithAdditionalAnnotations(targetAnnotation));
        }

        var filesToWrite = new Dictionary<DocumentId, string>();
        var referenceCount = 0;
        var rewrittenSourceRoot = annotatedSourceRoot;

        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            var isSource = document.Id == sourceDocument.Id;
            var modelRoot = isSource ? (SyntaxNode)sourceRoot : await document.GetSyntaxRootAsync(cancellationToken);
            var model = isSource ? sourceModel : await document.GetSemanticModelAsync(cancellationToken);
            if (modelRoot is null || model is null) continue;

            var candidates = modelRoot.DescendantNodes().OfType<NameSyntax>()
                .Where(node => SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(node, cancellationToken).Symbol, sourceSymbol))
                .Where(node => !IsInside(node, source.Span))
                .Where(node => document.Id != targetDocument.Id || !IsInside(node, targetNode.Span))
                .ToList();

            var candidateSet = new HashSet<SyntaxNode>(candidates);
            var outermost = candidates.Where(node => !node.Ancestors().Any(candidateSet.Contains)).ToList();
            if (outermost.Count == 0) continue;

            // The replacement happens in the tree that carries the declaration annotation; annotations do
            // not move text, so a candidate is found again by its span. Symbols are resolved on the model's
            // own tree, because a node from another tree cannot be resolved at all.
            var tree = isSource ? (SyntaxNode)annotatedSourceRoot : modelRoot;
            var nodesToReplace = new List<SyntaxNode>();
            if (isSource)
            {
                nodesToReplace.AddRange(outermost
                    .Select(node => tree.FindNode(node.Span, getInnermostNodeForTie: true))
                    .Where(node => node is not null)
                    .Select(node => node!));
            }
            else
            {
                nodesToReplace.AddRange(outermost);
            }

            var spelling = TypeSpelling(targetSymbol, modelRoot);
            var rewritten = tree.ReplaceNodes(nodesToReplace,
                (original, _) => ParseTypeName($"{spelling}.{typeName}").WithTriviaFrom(original));

            referenceCount += outermost.Count;
            filesToWrite[document.Id] = rewritten.ToFullString();
            if (isSource) rewrittenSourceRoot = rewritten as CompilationUnitSyntax ?? rewrittenSourceRoot;
        }

        // ── Move the declaration ─────────────────────────────────────────────────────────
        var movedNode = annotatedSourceRoot.GetAnnotatedNodes(annotation).OfType<BaseTypeDeclarationSyntax>().Single();
        var targetIndent = Indentation(targetNode) + "    ";
        var movedMember = ParseMemberDeclaration(Reindent(movedNode.NormalizeWhitespace().ToFullString(), targetIndent));
        if (movedMember is null) return new { error = $"'{typeName}' could not be re-parsed after normalising it." };

        movedMember = movedMember
            .WithLeadingTrivia(ElasticCarriageReturnLineFeed, Whitespace(targetIndent))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        if (targetDocument.Id == sourceDocument.Id)
        {
            // Both declarations live in one tree: the annotations survive the reference rewrite.
            var tree = rewrittenSourceRoot;
            var currentTarget = (TypeDeclarationSyntax)tree.GetAnnotatedNodes(targetAnnotation).Single();
            var currentSource = tree.GetAnnotatedNodes(annotation).OfType<BaseTypeDeclarationSyntax>().Single();

            var withoutSource = RemoveFromContainer(tree, currentSource);
            var targetToExpand = (TypeDeclarationSyntax)withoutSource.GetAnnotatedNodes(targetAnnotation).Single();
            var expanded = withoutSource.ReplaceNode(
                targetToExpand, targetToExpand.WithMembers(targetToExpand.Members.Add(movedMember)));

            filesToWrite[sourceDocument.Id] = expanded.ToFullString();
        }
        else
        {
            var sourceText = filesToWrite.TryGetValue(sourceDocument.Id, out var current)
                ? current
                : (await sourceDocument.GetTextAsync(cancellationToken)).ToString();

            var parsedSource = ParseCompilationUnit(sourceText);
            var currentSource = parsedSource.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .First(node => node.Identifier.ValueText == typeName &&
                               node.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax);

            var withoutSource = RemoveFromContainer(parsedSource, currentSource);
            filesToWrite[sourceDocument.Id] = withoutSource.ToFullString();

            var targetText = filesToWrite.TryGetValue(targetDocument.Id, out var targetCurrent)
                ? targetCurrent
                : (await targetDocument.GetTextAsync(cancellationToken)).ToString();

            var targetTree = ParseCompilationUnit(targetText);
            var currentTarget = (TypeDeclarationSyntax)targetTree.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .First(node => node.Identifier.ValueText.Equals(targetTypeName, StringComparison.OrdinalIgnoreCase) &&
                               node is TypeDeclarationSyntax);

            var expandedTarget = currentTarget.WithMembers(currentTarget.Members.Add(movedMember));
            filesToWrite[targetDocument.Id] = targetTree.ReplaceNode(currentTarget, expandedTarget).ToFullString();
        }

        // A file that held only the moved type has nothing left to declare: it is deleted with it.
        var deleteSource = false;
        if (filesToWrite.TryGetValue(sourceDocument.Id, out var sourceResult))
        {
            var remaining = ParseCompilationUnit(sourceResult);
            deleteSource = !remaining.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any() &&
                           !remaining.DescendantNodes().OfType<DelegateDeclarationSyntax>().Any() &&
                           !remaining.DescendantNodes().OfType<GlobalStatementSyntax>().Any();
        }

        // ── Gate: the whole solution, because the type's metadata name changed ───────────
        var projected = solution;
        foreach (var (documentId, text) in filesToWrite)
            projected = projected.WithDocumentText(documentId, SourceText.From(text));
        if (deleteSource) projected = projected.RemoveDocument(sourceDocument.Id);

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, solution.ProjectIds, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"moving '{typeName}' into '{targetTypeName}'");

        if (!dryRun)
        {
            foreach (var (documentId, text) in filesToWrite)
            {
                if (deleteSource && documentId == sourceDocument.Id) continue;

                var document = solution.GetDocument(documentId);
                if (document is not null)
                    await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, text, cancellationToken);
            }

            if (deleteSource)
            {
                if (sourceDocument.FilePath is not null && File.Exists(sourceDocument.FilePath))
                    File.Delete(sourceDocument.FilePath);
                _workspace.TryRemoveDocument(sourceDocument.Id);
            }
        }

        var files = new List<object>();
        foreach (var (documentId, text) in filesToWrite)
        {
            var document = solution.GetDocument(documentId);
            var oldText = document is null ? string.Empty : (await document.GetTextAsync(cancellationToken)).ToString();
            files.Add(new
            {
                file = document?.FilePath,
                preview = RefactoringFileWriter.Diff(oldText, text),
            });
        }

        return new
        {
            @refactoring = "move_type_to_nested_scope",
            type = sourceSymbol.ToDisplayString(),
            movedInto = targetSymbol.ToDisplayString(),
            file = sourceDocument.FilePath,
            targetFile = targetDocument.FilePath,
            referencesRewritten = referenceCount,
            sourceFileDeleted = deleteSource,
            filesChanged = filesToWrite.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            files,
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static Document? FindDocument(Solution solution, string filePath)
    {
        string path;
        try { path = Path.GetFullPath(filePath); }
        catch { return null; }

        return solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(document => document.FilePath is not null &&
                                       string.Equals(Path.GetFullPath(document.FilePath), path, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(SyntaxNode node, TextSpan span) =>
        span.Contains(node.Span.Start) && span.Contains(node.Span.End);

    /// <summary>The shortest spelling of the target type the file can see, without its own name.</summary>
    private static string TypeSpelling(INamedTypeSymbol target, SyntaxNode anchor)
    {
        var chain = new List<string>();
        for (var type = target; type is not null; type = type.ContainingType)
            chain.Insert(0, type.Name);

        var name = string.Join(".", chain);
        var namespaceSymbol = target.ContainingNamespace;
        if (namespaceSymbol is null || namespaceSymbol.IsGlobalNamespace) return name;

        var namespaceText = namespaceSymbol.ToDisplayString();
        return RefactoringNamespaces.IsVisible(anchor, namespaceText) ? name : $"global::{namespaceText}.{name}";
    }

    private static CompilationUnitSyntax RemoveFromContainer(CompilationUnitSyntax root, BaseTypeDeclarationSyntax declaration)
    {
        switch (declaration.Parent)
        {
            case CompilationUnitSyntax unit:
                return unit.WithMembers(unit.Members.Remove(declaration));
            case BaseNamespaceDeclarationSyntax @namespace:
                return root.ReplaceNode(@namespace, @namespace.WithMembers(@namespace.Members.Remove(declaration)));
            default:
                return root;
        }
    }

    private static string Indentation(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }

    private static string Reindent(string text, string indentation)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) =>
            index == 0 || line.Length == 0 ? line : indentation + line));
    }
}
