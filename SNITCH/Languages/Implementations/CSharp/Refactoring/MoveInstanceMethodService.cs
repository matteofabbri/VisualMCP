using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Move instance method": the method becomes a static method of the target type with the source
/// instance as its first parameter, its own instance state is re-pointed at that parameter, and every call
/// site gains the receiver as the first argument.
/// </summary>
public interface IMoveInstanceMethodService
{
    Task<object> MoveInstanceMethod(
        string filePath,
        string typeName,
        string methodName,
        string targetTypeName,
        string receiverName = "self",
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class MoveInstanceMethodService : IMoveInstanceMethodService
{
    private readonly IRoslynWorkspace _workspace;

    public MoveInstanceMethodService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MoveInstanceMethod(
        string filePath,
        string typeName,
        string methodName,
        string targetTypeName,
        string receiverName = "self",
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The source type is required." };
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "The method to move is required." };
        if (string.IsNullOrWhiteSpace(targetTypeName)) return new { error = "The target type is required." };
        if (!SyntaxFacts.IsValidIdentifier(receiverName)) return new { error = $"'{receiverName}' is not a valid parameter name." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.MoveInstanceMethodAsync(
                _workspace, filePath, typeName, methodName, targetTypeName, receiverName, dryRun, cancellationToken);

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
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (source is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(path)}." };

        if (model.GetDeclaredSymbol(source, cancellationToken) is not INamedTypeSymbol sourceSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var candidates = source.Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .ToList();
        if (candidates.Count == 0) return new { error = $"No method '{methodName}' in '{typeName}'." };
        if (candidates.Count > 1) return new { error = $"'{methodName}' has overloads; move them one at a time." };

        var method = candidates[0];
        if (model.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol methodSymbol)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var refusal = RefuseFor(method, methodSymbol);
        if (refusal is not null) return new { error = refusal };

        if (method.Body is null && method.ExpressionBody is null)
            return new { error = $"'{methodName}' has no body to move." };

        var targetSymbol = await FindTypeAsync(solution, targetTypeName, cancellationToken);
        if (targetSymbol is null) return new { error = $"No single type named '{targetTypeName}' in the solution." };
        if (targetSymbol.Equals(sourceSymbol, SymbolEqualityComparer.Default))
            return new { error = $"'{typeName}' is already the source and the target." };

        if (targetSymbol.GetMembers(methodName).Length > 0)
            return new { error = $"'{targetSymbol.ToDisplayString()}' already has a member named '{methodName}'." };

        var targetReference = targetSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (targetReference?.SyntaxTree is null) return new { error = $"'{targetSymbol.ToDisplayString()}' is not declared in source." };

        var targetDocument = await FindDocumentByTreeAsync(solution, targetReference.SyntaxTree, cancellationToken);
        if (targetDocument is null) return new { error = "Could not locate the file that declares the target type." };

        if (method.ParameterList.Parameters.Any(parameter => parameter.Identifier.ValueText == receiverName))
            return new { error = $"'{methodName}' already has a parameter named '{receiverName}'; pass another receiverName." };

        // ── Rewrite the body: `this` and implicit instance members go through the receiver ──
        var rewritten = RewriteBody(method, model, sourceSymbol, methodSymbol, receiverName, cancellationToken);
        if (rewritten.Error is not null) return new { error = rewritten.Error };

        var receiverParameter = ParseParameterList($"{typeName} {receiverName}").Parameters[0];
        var staticModifiers = method.Modifiers
            .Where(modifier => !modifier.IsKind(SyntaxKind.VirtualKeyword) &&
                               !modifier.IsKind(SyntaxKind.OverrideKeyword) &&
                               !modifier.IsKind(SyntaxKind.AbstractKeyword))
            .ToList();
        staticModifiers.Add(Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(Space));

        var movedMethod = rewritten.Method!
            .WithParameterList(rewritten.Method!.ParameterList.WithParameters(
                rewritten.Method.ParameterList.Parameters.Insert(0, receiverParameter)))
            .WithModifiers(TokenList(staticModifiers));

        // ── Call sites ────────────────────────────────────────────────────────────────────
        var targetName = targetSymbol.Name;
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        var edits = new Dictionary<DocumentId, Dictionary<SyntaxNode, SyntaxNode>>();
        var documents = new Dictionary<DocumentId, Document>();
        var unsupported = new List<string>();

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (method.Span.Contains(location.Location.SourceSpan)) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null) continue;

                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Expression.DescendantNodesAndSelf().Contains(node))
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (method group or nameof)");
                    continue;
                }

                SyntaxNode replaceTarget;
                ExpressionSyntax invoked;
                ExpressionSyntax receiver;

                if (invocation.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == methodName)
                {
                    replaceTarget = invocation;
                    invoked = ParseExpression($"{targetName}.{access.Name.WithoutTrivia()}");
                    receiver = access.Expression;
                }
                else if (invocation.Expression is IdentifierNameSyntax || invocation.Expression is GenericNameSyntax)
                {
                    // A bare call can only come from inside the source type, where `this` is available.
                    replaceTarget = invocation;
                    invoked = ParseExpression($"{targetName}.{invocation.Expression.WithoutTrivia()}");
                    receiver = ThisExpression();
                }
                else
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (unexpected call form)");
                    continue;
                }

                var arguments = invocation.ArgumentList.Arguments.Insert(0, Argument(receiver.WithoutTrivia()));
                var replacement = InvocationExpression(invoked.WithoutTrivia(),
                    invocation.ArgumentList.WithArguments(arguments));

                if (!edits.TryGetValue(location.Document.Id, out var map))
                {
                    map = new Dictionary<SyntaxNode, SyntaxNode>();
                    edits[location.Document.Id] = map;
                    documents[location.Document.Id] = location.Document;
                }

                map[replaceTarget] = replacement.WithTriviaFrom(replaceTarget);
            }
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) of '{methodName}' are not plain calls; moving would change their meaning.",
                uses = unsupported.Take(10).ToList(),
            };

        // ── One working tree per document ─────────────────────────────────────────────────
        // Call sites first (their nodes come from the semantic model), then the method and the target
        // type are located by name: after a rebuild the old nodes are no longer tree key.
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

        var methodNow = sourceTypeNow.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == methodName);
        if (methodNow is null) return new { error = "The method was lost while the edits were applied." };

        working[document.Id] = sourceRootNow.ReplaceNode(sourceTypeNow,
            sourceTypeNow.WithMembers(List(sourceTypeNow.Members.Where(member => member != methodNow))));

        var targetRootNow = working[targetDocument.Id];
        var targetTypeNow = targetRootNow.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == targetSymbol.Name);
        if (targetTypeNow is null) return new { error = "The target type was lost while the edits were applied." };

        var indentation = IndentationOf(targetTypeNow) + "    ";
        var inserted = movedMethod
            .WithLeadingTrivia(Whitespace(indentation))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        working[targetDocument.Id] = targetRootNow.ReplaceNode(
            targetTypeNow, targetTypeNow.WithMembers(targetTypeNow.Members.Add(inserted)));

        // ── Both namespaces have to be visible where they are now used ───────────────────
        var namespaces = new[]
        {
            targetSymbol.ContainingNamespace is { IsGlobalNamespace: false } targetNamespace ? targetNamespace.ToDisplayString() : null,
            sourceSymbol.ContainingNamespace is { IsGlobalNamespace: false } sourceNamespace ? sourceNamespace.ToDisplayString() : null,
        }.Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal);

        foreach (var documentId in working.Keys.ToList())
        {
            if (working[documentId] is not CompilationUnitSyntax unit) continue;

            foreach (var needed in namespaces)
                if (!RefactoringNamespaces.IsVisible(unit, needed))
                    unit = RefactoringNamespaces.AddUsing(unit, needed);

            working[documentId] = unit;
        }

        // ── Write / gate ─────────────────────────────────────────────────────────────────
        var finalEdits = new List<(Document Document, string NewText, string Preview)>();
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

            finalEdits.Add((editedDocument, after, RefactoringFileWriter.Diff(before, after)));
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
            @refactoring = "move_instance_method",
            from = $"{sourceSymbol.ToDisplayString()}.{methodName}",
            to = targetSymbol.ToDisplayString(),
            receiver = receiverName,
            callSites = references.Sum(reference => reference.Locations.Count(location => location.Location.IsInSource)),
            filesChanged = finalEdits.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = finalEdits.Select(edit => new { file = edit.Document.FilePath, preview = edit.Preview }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? RefuseFor(MethodDeclarationSyntax method, IMethodSymbol symbol)
    {
        if (symbol.MethodKind != MethodKind.Ordinary) return $"'{symbol.Name}' is a {symbol.MethodKind}, not an ordinary method.";
        if (symbol.IsStatic) return $"'{symbol.Name}' is static; use move_static_members.";
        if (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride)
            return $"'{symbol.Name}' is abstract/virtual/override: its contract with the hierarchy would break.";
        if (symbol.IsExtern) return $"'{symbol.Name}' is extern.";
        if (symbol.ExplicitInterfaceImplementations.Length > 0) return $"'{symbol.Name}' implements an interface explicitly.";
        if (method.Body is not null && method.Body.DescendantNodes().OfType<BaseExpressionSyntax>().Any())
            return $"'{symbol.Name}' uses 'base'; a static method in another type has no base to call.";
        if (symbol.MethodKind == MethodKind.Destructor) return "A destructor cannot be moved.";

        return null;
    }

    private static (MethodDeclarationSyntax? Method, string? Error) RewriteBody(
        MethodDeclarationSyntax method,
        SemanticModel model,
        INamedTypeSymbol sourceType,
        IMethodSymbol methodSymbol,
        string receiverName,
        CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var node in (method.Body ?? (SyntaxNode?)method.ExpressionBody)!.DescendantNodesAndSelf())
        {
            if (node is ThisExpressionSyntax)
            {
                replacements[node] = IdentifierName(receiverName);
                continue;
            }

            if (node is not IdentifierNameSyntax identifier) continue;

            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
            if (symbol.IsStatic) continue;
            if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, sourceType)) continue;
            if (SymbolEqualityComparer.Default.Equals(symbol, methodSymbol)) continue;

            // `x.Member` already has a receiver; only the implicit-this form needs the prefix.
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier) continue;

            // The moved method lives in another type: private/protected state of the source is out of
            // reach there, so the move is refused instead of producing code that does not compile.
            if (symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                return (null, $"'{symbol.Name}' is {symbol.DeclaredAccessibility.ToString().ToLowerInvariant()}; " +
                              "the moved method would not be able to read it from another type.");

            replacements[identifier] = ParseExpression($"{receiverName}.{identifier.Identifier.ValueText}");
        }

        if (replacements.Count == 0) return (method, null);

        if (method.Body is not null)
        {
            var body = method.Body.ReplaceNodes(replacements.Keys, (original, _) => replacements[original].WithTriviaFrom(original));
            return (method.WithBody(body), null);
        }

        var expression = method.ExpressionBody!.Expression
            .ReplaceNodes(replacements.Keys, (original, _) => replacements[original].WithTriviaFrom(original));

        return (method.WithExpressionBody(method.ExpressionBody.WithExpression(expression)), null);
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
