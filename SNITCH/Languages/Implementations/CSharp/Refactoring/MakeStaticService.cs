using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>Rider's "Make static" (CSharpMakeStatic): a member that never touches instance state becomes static, call sites included.</summary>
public interface IMakeStaticService
{
    Task<object> MakeStatic(
        string filePath,
        string memberName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MakeStaticService : IMakeStaticService
{
    private readonly IRoslynWorkspace _workspace;

    public MakeStaticService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MakeStatic(
        string filePath,
        string memberName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(memberName)) return new { error = "A member name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var diskText = await File.ReadAllTextAsync(path, cancellationToken);
        var modelledText = (await document.GetTextAsync(cancellationToken)).ToString();
        if (!string.Equals(diskText, modelledText, StringComparison.Ordinal))
            return new { error = $"{Path.GetFileName(path)} changed on disk after the solution was loaded; the in-memory model is stale. Call load_solution, then retry." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var declarations = root.DescendantNodes()
            .Where(node => node switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText == memberName,
                PropertyDeclarationSyntax property => property.Identifier.ValueText == memberName,
                _ => false,
            })
            .ToList();

        if (declarations.Count == 0)
            return new { error = $"No method or property '{memberName}' in {Path.GetFileName(path)}." };
        if (declarations.Count > 1)
            return new { error = $"Several methods named '{memberName}' (overloads) in the file; make_static needs the single declaration." };

        var declaration = declarations[0];
        var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
        if (symbol is null) return new { error = $"'{memberName}' did not resolve to a symbol." };

        var refusal = RefuseFor(symbol);
        if (refusal is not null) return new { error = refusal };

        var owner = symbol.ContainingType;
        if (owner is null) return new { error = $"'{memberName}' has no containing type." };

        var instanceUse = FindInstanceUse(declaration, model, owner, cancellationToken);
        if (instanceUse is not null)
            return new { error = $"'{memberName}' touches instance state ({instanceUse}); it cannot become static." };

        // ── Collect the documents that must change ─────────────────────────────────────────
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var affected = new Dictionary<DocumentId, Document>();
        var unsupported = new List<string>();
        var referenceCount = 0;

        affected[document.Id] = document;

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id == document.Id && declaration.Span.Contains(location.Location.SourceSpan)) continue;
                if (!affected.ContainsKey(location.Document.Id)) affected[location.Document.Id] = location.Document;
                referenceCount++;
            }
        }

        // ── Build every new text first, so no write can invalidate a later read ────────────
        var edits = new List<(Document Document, string NewText, int Replacements, string Preview)>();

        foreach (var (documentId, target) in affected)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var receivers = targetRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(candidate => SymbolEqualityComparer.Default.Equals(
                    targetModel.GetSymbolInfo(candidate, cancellationToken).Symbol, symbol))
                .ToList();

            foreach (var memberAccess in receivers)
            {
                if (IsConditionalAccess(memberAccess))
                {
                    unsupported.Add($"{Path.GetFileName(target.FilePath)}:{Line(memberAccess)} (conditional access '?.')");
                }
            }

            if (unsupported.Count > 0) continue;

            var newRoot = receivers.Count == 0
                ? targetRoot
                : targetRoot.ReplaceNodes(receivers, (original, _) => original.WithExpression(TypeReference(owner, original)));

            var localDeclaration = documentId == document.Id
                ? newRoot.DescendantNodes()
                    .FirstOrDefault(node => node is MethodDeclarationSyntax or PropertyDeclarationSyntax &&
                                            node.Span == declaration.Span)
                : null;

            if (documentId == document.Id && localDeclaration is not null)
            {
                newRoot = newRoot.ReplaceNode(
                    localDeclaration,
                    localDeclaration switch
                    {
                        MethodDeclarationSyntax method => method.WithModifiers(WithStatic(method.Modifiers)),
                        PropertyDeclarationSyntax property => property.WithModifiers(WithStatic(property.Modifiers)),
                        _ => localDeclaration,
                    });
            }

            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((target, newText, receivers.Count, RefactoringFileWriter.Diff(oldText, newText)));
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} reference(s) cannot be expressed as a static call; nothing was written.",
                references = unsupported.Take(10).ToList(),
            };

        if (edits.Count == 0)
            return new { error = "Nothing to rewrite: the declaration is the only occurrence and it already matched." };

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "make_static",
            member = symbol.ToDisplayString(),
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            referencesRewritten = referenceCount,
            filesChanged = edits.Count,
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                replacements = edit.Replacements,
                preview = edit.Preview,
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? RefuseFor(ISymbol symbol)
    {
        if (symbol.IsStatic) return $"'{symbol.Name}' is already static.";
        if (symbol.ContainingType?.TypeKind == TypeKind.Interface) return "Interface members cannot be made static this way.";

        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind != MethodKind.Ordinary) return $"'{method.Name}' is a {method.MethodKind}, not an ordinary method.";
            if (method.IsAbstract || method.IsVirtual || method.IsOverride) return $"'{method.Name}' is abstract/virtual/override: overrides would break.";
            if (method.IsExtern) return $"'{method.Name}' is extern.";
            if (method.ExplicitInterfaceImplementations.Length > 0) return $"'{method.Name}' implements an interface explicitly.";
        }

        if (symbol is IPropertySymbol property && (property.IsAbstract || property.IsVirtual || property.IsOverride))
            return $"'{property.Name}' is abstract/virtual/override: overrides would break.";

        if (symbol.ContainingType is { } owner && owner.AllInterfaces.Length > 0)
        {
            foreach (var interfaceMember in owner.AllInterfaces.SelectMany(i => i.GetMembers(symbol.Name)))
            {
                var implementation = owner.FindImplementationForInterfaceMember(interfaceMember);
                if (SymbolEqualityComparer.Default.Equals(implementation, symbol))
                    return $"'{symbol.Name}' implements the interface member '{interfaceMember.ContainingType.Name}.{interfaceMember.Name}'.";
            }
        }

        return null;
    }

    /// <summary>First place where the member's own body reads instance state, or null when it is pure.</summary>
    private static string? FindInstanceUse(
        SyntaxNode declaration, SemanticModel model, INamedTypeSymbol owner, CancellationToken cancellationToken)
    {
        foreach (var node in declaration.DescendantNodes())
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                return $"'this'/'base' at line {Line(node)}";

            if (node is not (IdentifierNameSyntax or GenericNameSyntax)) continue;

            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
            if (symbol is null || symbol.IsStatic) continue;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol) continue;
            if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;

            var containing = symbol.ContainingType;
            if (containing is null || !IsOwnerOrBase(containing, owner)) continue;

            // `other.Member` reaches a *different* instance through a local/parameter: that is fine.
            if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node &&
                access.Expression is not (ThisExpressionSyntax or BaseExpressionSyntax))
            {
                continue;
            }

            return $"{symbol.Name} at line {Line(node)}";
        }

        return null;
    }

    private static bool IsOwnerOrBase(INamedTypeSymbol candidate, INamedTypeSymbol owner)
    {
        for (var type = owner; type is not null; type = type.BaseType)
            if (SymbolEqualityComparer.Default.Equals(type, candidate)) return true;

        return false;
    }

    private static bool IsConditionalAccess(MemberAccessExpressionSyntax memberAccess)
        => memberAccess.Parent?.Parent is ConditionalAccessExpressionSyntax;

    /// <summary>
    /// The shortest spelling of the type that the file can see. The anchor must be the reference
    /// itself (not the compilation unit): a file-scoped or block namespace is a descendant of the
    /// root, so anchoring at the root would always fall back to the fully-qualified name.
    /// </summary>
    private static ExpressionSyntax TypeReference(INamedTypeSymbol type, SyntaxNode anchor)
    {
        var namespaceSymbol = type.ContainingNamespace;
        if (namespaceSymbol is null || namespaceSymbol.IsGlobalNamespace) return IdentifierName(type.Name);

        var namespaceText = namespaceSymbol.ToDisplayString();
        var unit = anchor.AncestorsAndSelf().OfType<CompilationUnitSyntax>().FirstOrDefault();

        var enclosingNamespace = anchor.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var sameNamespace = enclosingNamespace is not null &&
                            (string.Equals(enclosingNamespace, namespaceText, StringComparison.Ordinal) ||
                             enclosingNamespace.StartsWith(namespaceText + ".", StringComparison.Ordinal));
        var imported = unit?.Usings.Any(usingDirective =>
            usingDirective.Alias is null &&
            string.Equals(usingDirective.Name?.ToString(), namespaceText, StringComparison.Ordinal)) == true;

        return sameNamespace || imported
            ? IdentifierName(type.Name)
            : ParseExpression($"global::{namespaceText}.{type.Name}");
    }

    private static SyntaxTokenList WithStatic(SyntaxTokenList modifiers)
    {
        var staticToken = Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(Space);

        // `static` reads better before `async`/`unsafe`; C# accepts either order.
        for (var index = 0; index < modifiers.Count; index++)
        {
            if (modifiers[index].IsKind(SyntaxKind.AsyncKeyword) || modifiers[index].IsKind(SyntaxKind.UnsafeKeyword))
                return modifiers.Insert(index, staticToken);
        }

        return modifiers.Add(staticToken);
    }

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
