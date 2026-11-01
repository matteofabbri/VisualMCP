using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move static members": static methods, properties and fields move to another type and every
/// reference is re-pointed — both the qualified <c>Source.M(...)</c> form and the bare <c>M(...)</c> form
/// used inside the source type.
/// </summary>
public interface IMoveStaticMembersService
{
    Task<object> MoveStaticMembers(
        string filePath,
        string sourceTypeName,
        string[] memberNames,
        string targetTypeName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MoveStaticMembersService : IMoveStaticMembersService
{
    private readonly IRoslynWorkspace _workspace;

    public MoveStaticMembersService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveStaticMembers(
        string filePath,
        string sourceTypeName,
        string[] memberNames,
        string targetTypeName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(sourceTypeName)) return new { error = "The source type is required." };
        if (memberNames is null || memberNames.Length == 0) return new { error = "Name the members to move." };
        if (string.IsNullOrWhiteSpace(targetTypeName)) return new { error = "The target type is required." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.MoveStaticMembersAsync(
                _workspace, filePath, sourceTypeName, memberNames, targetTypeName, dryRun, cancellationToken);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = FindDocument(solution, path);
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var source = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == sourceTypeName);
        if (source is null) return new { error = $"No type '{sourceTypeName}' in {Path.GetFileName(path)}." };

        if (model.GetDeclaredSymbol(source, cancellationToken) is not INamedTypeSymbol sourceSymbol)
            return new { error = $"'{sourceTypeName}' did not resolve to a type." };
        if (sourceSymbol.Arity > 0)
            return new { error = $"'{sourceTypeName}' is generic; moving members out of a generic type is not supported." };

        var targetSymbol = await FindTypeAsync(solution, targetTypeName, cancellationToken);
        if (targetSymbol is null) return new { error = $"No type named '{targetTypeName}' in the solution." };
        if (targetSymbol.Equals(sourceSymbol, SymbolEqualityComparer.Default))
            return new { error = $"'{sourceTypeName}' is already the target." };

        var targetReference = targetSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (targetReference?.SyntaxTree is null)
            return new { error = $"'{targetSymbol.ToDisplayString()}' is not declared in source." };

        var targetDocument = await FindDocumentByTreeAsync(solution, targetReference.SyntaxTree, cancellationToken);
        if (targetDocument is null) return new { error = "Could not locate the file that declares the target type." };

        // ── The selection ─────────────────────────────────────────────────────────────────
        var selected = new List<MemberDeclarationSyntax>();
        var symbols = new List<ISymbol>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var member = source.Members.FirstOrDefault(candidate => NameOf(candidate) == name);
            if (member is null)
                return new
                {
                    error = $"No movable member '{name}' in '{sourceTypeName}' (methods, properties and single-variable fields only).",
                    available = source.Members.Select(NameOf).Where(candidate => candidate is not null).Distinct().ToList(),
                };

            var symbol = SymbolOf(member, model, cancellationToken);
            if (symbol is null) return new { error = $"'{name}' did not resolve." };
            if (!symbol.IsStatic) return new { error = $"'{name}' is an instance member; use move_instance_method." };

            if (targetSymbol.GetMembers(name).Length > 0)
                return new { error = $"'{targetSymbol.ToDisplayString()}' already has a member named '{name}'." };

            selected.Add(member);
            symbols.Add(symbol);
        }

        // ── Reference rewrites, keyed by document ─────────────────────────────────────────
        var targetName = targetSymbol.Name;
        var targetNamespace = targetSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;
        var edits = new Dictionary<DocumentId, Dictionary<SyntaxNode, SyntaxNode>>();
        var documents = new Dictionary<DocumentId, Document>();

        foreach (var symbol in symbols)
        {
            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            {
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    if (selected.Any(member => member.Span.Contains(location.Location.SourceSpan))) continue;

                    var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                    if (referenceRoot is null) continue;

                    var node = referenceRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    var nameNode = node.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault() ?? node;

                    SyntaxNode replaceTarget;
                    SyntaxNode replacement;

                    if (nameNode.Parent is MemberAccessExpressionSyntax access && access.Name == nameNode)
                    {
                        replaceTarget = access;
                        replacement = access.WithExpression(ParseExpression(targetName).WithTriviaFrom(access.Expression));
                    }
                    else
                    {
                        replaceTarget = nameNode;
                        replacement = ParseExpression($"{targetName}.{nameNode.WithoutTrivia()}");
                    }

                    if (!edits.TryGetValue(location.Document.Id, out var map))
                    {
                        map = new Dictionary<SyntaxNode, SyntaxNode>();
                        edits[location.Document.Id] = map;
                        documents[location.Document.Id] = location.Document;
                    }

                    if (map.Keys.Any(existing => existing.Span.Contains(replaceTarget.Span))) continue;
                    foreach (var contained in map.Keys.Where(existing => replaceTarget.Span.Contains(existing.Span)).ToList())
                        map.Remove(contained);

                    map[replaceTarget] = replacement.WithTriviaFrom(replaceTarget);
                }
            }
        }

        // ── One working tree per document, edited in a fixed order ────────────────────────
        // References FIRST: their nodes are the ones the semantic model produced, so they are still
        // in the tree. Every later step finds the types by name, never by a stale node or span.
        var working = new Dictionary<DocumentId, SyntaxNode> { [document.Id] = root };
        if (targetDocument.Id != document.Id)
        {
            var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
            if (targetRoot is null) return new { error = "Could not read the target document." };
            working[targetDocument.Id] = targetRoot;
        }

        foreach (var (documentId, map) in edits)
        {
            if (!working.TryGetValue(documentId, out var workingRoot))
            {
                workingRoot = await documents[documentId].GetSyntaxRootAsync(cancellationToken);
                if (workingRoot is null) continue;
            }

            working[documentId] = workingRoot.ReplaceNodes(map.Keys, (original, _) => map[original]);
        }

        var sourceRootNow = working[document.Id];
        var sourceTypeNow = sourceRootNow.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == sourceSymbol.Name);
        if (sourceTypeNow is null) return new { error = "The source type was lost while the edits were applied." };

        var movedNames = memberNames.ToHashSet(StringComparer.Ordinal);
        working[document.Id] = sourceRootNow.ReplaceNode(sourceTypeNow, sourceTypeNow.WithMembers(List(
            sourceTypeNow.Members.Where(member => NameOf(member) is not { } name || !movedNames.Contains(name)))));

        var targetRootNow = working[targetDocument.Id];
        var targetTypeNow = targetRootNow.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == targetSymbol.Name);
        if (targetTypeNow is null) return new { error = "The target type was lost while the edits were applied." };

        var indentation = IndentationOf(targetTypeNow) + "    ";
        var movedMembers = selected.Select(member => member
            .NormalizeWhitespace()
            .WithLeadingTrivia(Whitespace(indentation))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed)).ToList();

        working[targetDocument.Id] = targetRootNow.ReplaceNode(
            targetTypeNow, targetTypeNow.WithMembers(targetTypeNow.Members.AddRange(movedMembers)));

        // ── The target namespace has to be visible in every touched file ─────────────────
        if (targetNamespace is not null)
        {
            foreach (var documentId in working.Keys.ToList())
            {
                if (working[documentId] is not CompilationUnitSyntax unit) continue;
                if (RefactoringNamespaces.IsVisible(unit, targetNamespace)) continue;

                working[documentId] = RefactoringNamespaces.AddUsing(unit, targetNamespace);
            }
        }

        // ── Write / gate ─────────────────────────────────────────────────────────────────
        var finalEdits = new List<(Document Document, string NewText, int Rewrites, string Preview)>();
        foreach (var (documentId, workingRoot) in working)
        {
            var editedDocument = documentId == document.Id
                ? document
                : documentId == targetDocument.Id
                    ? targetDocument
                    : documents[documentId];

            var before = (await editedDocument.GetTextAsync(cancellationToken)).ToString();
            var after = workingRoot.ToFullString();
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;

            finalEdits.Add((editedDocument, after,
                edits.TryGetValue(documentId, out var map) ? map.Count : 0,
                RefactoringFileWriter.Diff(before, after)));
        }

        if (finalEdits.Count == 0) return new { error = "Nothing to rewrite." };

        var projected = solution;
        foreach (var edit in finalEdits) projected = projected.WithDocumentText(edit.Document.Id, SourceText.From(edit.NewText));

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, finalEdits.Select(edit => edit.Document.Project.Id), cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the move");

        if (!dryRun)
            foreach (var edit in finalEdits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);

        return new
        {
            @refactoring = "move_static_members",
            from = sourceSymbol.ToDisplayString(),
            to = targetSymbol.ToDisplayString(),
            members = memberNames,
            filesChanged = finalEdits.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = finalEdits.Select(edit => new { file = edit.Document.FilePath, rewrites = edit.Rewrites, preview = edit.Preview }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static Document? FindDocument(Solution solution, string path)
    {
        foreach (var project in solution.Projects)
            foreach (var candidate in project.Documents)
                if (candidate.FilePath is not null &&
                    string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase))
                    return candidate;

        return null;
    }

    private static async Task<Document?> FindDocumentByTreeAsync(Solution solution, SyntaxTree tree, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
            foreach (var candidate in project.Documents)
                if (await candidate.GetSyntaxTreeAsync(cancellationToken) == tree)
                    return candidate;

        return null;
    }

    private static async Task<INamedTypeSymbol?> FindTypeAsync(Solution solution, string name, CancellationToken cancellationToken)
    {
        var found = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type, cancellationToken: cancellationToken);

        var types = found.OfType<INamedTypeSymbol>().ToList();
        if (name.Contains('.')) types = types.Where(type => type.ToDisplayString() == name).ToList();

        return types.Count == 1 ? types[0] : null;
    }

    private static string? NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field when field.Declaration.Variables.Count == 1 => field.Declaration.Variables[0].Identifier.ValueText,
        _ => null,
    };

    private static ISymbol? SymbolOf(MemberDeclarationSyntax member, SemanticModel model, CancellationToken cancellationToken) =>
        member switch
        {
            MethodDeclarationSyntax method => model.GetDeclaredSymbol(method, cancellationToken),
            PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property, cancellationToken),
            FieldDeclarationSyntax { Declaration.Variables.Count: 1 } field =>
                model.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken),
            _ => null,
        };

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }

    private static IEnumerable<INamedTypeSymbol> Flatten(INamespaceSymbol root)
    {
        foreach (var type in root.GetTypeMembers()) yield return type;
        foreach (var child in root.GetNamespaceMembers())
            foreach (var type in Flatten(child))
                yield return type;
    }
}
