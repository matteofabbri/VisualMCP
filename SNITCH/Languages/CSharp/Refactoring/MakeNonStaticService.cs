using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// The inverse of "Make static": a static member becomes an instance member, and every call site is
/// routed through an instance of the containing type.
/// </summary>
/// <remarks>
/// The design decision Rider's refactoring also takes: a call site can only be rewritten when an
/// instance of the type is already in scope there — <c>this</c> inside the type's own instance members,
/// otherwise the nearest local, parameter or field of that type (<see cref="InstanceResolution"/>).
/// Nothing is fabricated: when one call site has no such instance the whole operation is refused and
/// the call sites are listed, because the alternative (inventing <c>new Type()</c> per call) would
/// silently change behaviour.
/// </remarks>
public interface IMakeNonStaticService
{
    Task<object> MakeNonStatic(
        string filePath,
        string memberName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MakeNonStaticService : IMakeNonStaticService
{
    private readonly IRoslynWorkspace _workspace;

    public MakeNonStaticService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MakeNonStatic(
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

        if (containingType is not null)
        {
            declarations = declarations
                .Where(declaration => declaration.Ancestors().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault()?.Identifier.ValueText.Equals(containingType, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
        }

        if (declarations.Count == 0)
            return new { error = $"No method or property '{memberName}' in {Path.GetFileName(path)}." };
        if (declarations.Count > 1)
            return new { error = $"Several members named '{memberName}' in the file; make_non_static needs the single declaration (pass containingType, or read the symbol first)." };

        var declaration = declarations[0];
        var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
        if (symbol is null) return new { error = $"'{memberName}' did not resolve to a symbol." };

        var refusal = RefuseFor(symbol);
        if (refusal is not null) return new { error = refusal };

        var owner = symbol.ContainingType!;

        // ── Every reference must be reachable from an instance at its own position ────────
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var locations = references
            .SelectMany(reference => reference.Locations)
            .Where(location => location.Location.IsInSource)
            .Where(location => !(location.Document.Id == document.Id && declaration.Span.Contains(location.Location.SourceSpan)))
            .DistinctBy(location => (location.Document.Id, location.Location.SourceSpan))
            .ToList();

        var plans = new List<ReferencePlan>();
        var withoutInstance = new List<string>();

        foreach (var location in locations)
        {
            var referenceDocument = location.Document;
            var referenceRoot = await referenceDocument.GetSyntaxRootAsync(cancellationToken);
            var referenceModel = await referenceDocument.GetSemanticModelAsync(cancellationToken);
            if (referenceRoot is null || referenceModel is null || referenceDocument.FilePath is null) continue;

            var node = referenceRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;

            if (IsInsideNameOf(node))
            {
                // nameof(Type.M) binds without an instance and keeps binding after the change.
                plans.Add(new ReferencePlan(referenceDocument, node, null, "nameof"));
                continue;
            }

            var memberAccess = node.Parent as MemberAccessExpressionSyntax;
            var qualifiedByType = memberAccess is not null && memberAccess.Name == node;

            if (!qualifiedByType && InstanceResolution.IsInstancePosition(node) &&
                InstanceResolution.EnclosingType(node, referenceModel, cancellationToken) is { } enclosing &&
                InstanceResolution.IsOrDerivesFrom(enclosing, owner))
            {
                // An unqualified call inside the type's own instance members already means this.M.
                plans.Add(new ReferencePlan(referenceDocument, node, null, "implicit this"));
                continue;
            }

            var instance = InstanceResolution.Find(node, referenceModel, owner, cancellationToken);
            if (instance is null)
            {
                var qualifier = qualifiedByType ? memberAccess!.Expression.ToString() : "unqualified";
                withoutInstance.Add($"{Path.GetFileName(referenceDocument.FilePath)}:{line} ({qualifier})");
                continue;
            }

            plans.Add(new ReferencePlan(referenceDocument, node, instance, instance.Kind));
        }

        if (withoutInstance.Count > 0)
            return new
            {
                error = $"{withoutInstance.Count} call site(s) have no instance of '{owner.Name}' in scope, so they cannot become " +
                        "instance calls. Add a parameter, local or field of that type at those sites (or pass one to the " +
                        "surrounding member), then retry. Nothing was written.",
                callSitesWithoutInstance = withoutInstance.Take(12).ToList(),
            };

        // ── Rewrite, one document at a time, with nodes that all belong to that document ──
        var annotation = new SyntaxAnnotation("make_non_static_declaration");
        var filesToWrite = new Dictionary<Document, string>();
        var callSites = new List<object>();

        // The declaration's own file always changes (it loses `static`), even when it holds no call site.
        var documents = plans.Select(plan => plan.Document).DistinctBy(candidate => candidate.Id).ToList();
        if (documents.All(candidate => candidate.Id != document.Id)) documents.Add(document);

        foreach (var target in documents)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            if (targetRoot is null || target.FilePath is null) continue;

            var newRoot = targetRoot;

            if (target.Id == document.Id)
            {
                var annotated = targetRoot.DescendantNodes()
                    .First(node => node is MethodDeclarationSyntax or PropertyDeclarationSyntax &&
                                   node.SpanStart == declaration.SpanStart && node.RawKind == declaration.RawKind);
                newRoot = targetRoot.ReplaceNode(annotated, annotated.WithAdditionalAnnotations(annotation));
            }

            foreach (var plan in plans
                         .Where(plan => plan.Document.Id == target.Id)
                         .OrderByDescending(plan => plan.Node.SpanStart))
            {
                if (plan.Receiver is null) continue;

                var current = newRoot.FindNode(plan.Node.Span, getInnermostNodeForTie: true);
                var (subject, replacement) = Qualify(current, plan.Receiver.Expression);
                if (subject is null || replacement is null) continue;

                callSites.Add(new
                {
                    file = target.FilePath,
                    line = plan.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    receiver = plan.Receiver.Expression,
                    receiverKind = plan.Receiver.Kind,
                    before = subject.ToString(),
                    after = replacement.ToString(),
                });

                newRoot = newRoot.ReplaceNode(subject, replacement);
            }

            if (target.Id == document.Id)
            {
                var currentDeclaration = newRoot.GetAnnotatedNodes(annotation).Single();
                newRoot = newRoot.ReplaceNode(currentDeclaration, WithoutStatic((MemberDeclarationSyntax)currentDeclaration));
            }

            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            filesToWrite[target] = newText;
        }

        if (filesToWrite.Count == 0)
            return new { error = "Nothing to rewrite: the declaration is the only occurrence and it already matched." };

        // ── Gate: every project that owns a changed file must still compile ──────────────
        var projected = solution;
        foreach (var (target, text) in filesToWrite)
            projected = projected.WithDocumentText(target.Id, Microsoft.CodeAnalysis.Text.SourceText.From(text));

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, filesToWrite.Keys.Select(target => target.Project.Id), cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "making the member an instance member");

        if (!dryRun)
        {
            foreach (var (target, text) in filesToWrite)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, target, text, cancellationToken);
        }

        return new
        {
            @refactoring = "make_non_static",
            member = symbol.ToDisplayString(),
            containingType = owner.ToDisplayString(),
            file = document.FilePath,
            referencesRewritten = plans.Count(plan => plan.Receiver is not null),
            implicitThis = plans.Count(plan => plan.Receiver is null && plan.Kind == "implicit this"),
            nameofReferences = plans.Count(plan => plan.Kind == "nameof"),
            filesChanged = filesToWrite.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            callSites,
            files = filesToWrite.Select(entry => new
            {
                file = entry.Key.FilePath,
                preview = RefactoringFileWriter.Diff(
                    File.Exists(entry.Key.FilePath!) ? File.ReadAllText(entry.Key.FilePath!) : string.Empty, entry.Value),
            }).ToList(),
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? RefuseFor(ISymbol symbol)
    {
        if (!symbol.IsStatic) return $"'{symbol.Name}' is already an instance member.";
        if (symbol.ContainingType is null) return $"'{symbol.Name}' has no containing type.";

        if (symbol.ContainingType.IsStatic)
            return $"'{symbol.ContainingType.Name}' is a static class: it cannot declare instance members.";

        switch (symbol)
        {
            case IMethodSymbol method:
                if (method.MethodKind != MethodKind.Ordinary)
                    return $"'{method.Name}' is a {method.MethodKind}; only ordinary methods and properties can become instance members.";
                if (method.IsExtensionMethod)
                    return $"'{method.Name}' is an extension method. Use extension_to_static first, then make_non_static on the plain method.";
                if (method.IsAbstract || method.IsVirtual || method.IsOverride)
                    return $"'{method.Name}' is abstract/virtual/override: call sites cannot be reduced to one instance.";
                if (method.ExplicitInterfaceImplementations.Length > 0)
                    return $"'{method.Name}' implements an interface member explicitly.";
                if (method.IsExtern) return $"'{method.Name}' is extern.";
                break;

            case IPropertySymbol property:
                if (property.IsAbstract || property.IsVirtual || property.IsOverride)
                    return $"'{property.Name}' is abstract/virtual/override: call sites cannot be reduced to one instance.";
                if (property.IsIndexer) return "'make_non_static' does not handle indexers.";
                break;

            default:
                return $"'{symbol.Name}' is a {symbol.Kind}; only methods and properties can become instance members.";
        }

        if (symbol.ContainingType.TypeKind == TypeKind.Interface)
            return "Interface members cannot be made instance members this way.";

        return null;
    }

    /// <summary>
    /// For a member access <c>Type.M</c> the qualifier is replaced; an unqualified use becomes
    /// <c>receiver.M</c>. Returns the node to replace and its replacement.
    /// </summary>
    private static (SyntaxNode? Subject, SyntaxNode? Replacement) Qualify(SyntaxNode node, string receiver)
    {
        if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node)
        {
            var expression = ParseExpression(receiver).WithTriviaFrom(access.Expression);
            return (access.Expression, expression);
        }

        var name = node switch
        {
            IdentifierNameSyntax identifier => (SimpleNameSyntax)identifier.WithoutTrivia(),
            GenericNameSyntax generic => generic.WithoutTrivia(),
            _ => null,
        };

        if (name is null) return (null, null);

        return (node, MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, ParseExpression(receiver), name)
            .WithTriviaFrom(node));
    }

    /// <summary>Removes the declaration's `static` token, keeping the member's own indentation.</summary>
    private static MemberDeclarationSyntax WithoutStatic(MemberDeclarationSyntax declaration)
    {
        var kept = declaration.Modifiers.Where(token => !token.IsKind(SyntaxKind.StaticKeyword)).ToList();
        if (kept.Count == declaration.Modifiers.Count) return declaration;

        var rewritten = declaration.WithModifiers(TokenList(kept));
        if (declaration.Modifiers.Count > 0 && declaration.Modifiers[0].IsKind(SyntaxKind.StaticKeyword))
            rewritten = rewritten.WithLeadingTrivia(declaration.GetLeadingTrivia());

        return rewritten;
    }

    private static bool IsInsideNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });

    private sealed record ReferencePlan(Document Document, SyntaxNode Node, ResolvedInstance? Receiver, string Kind);
}
