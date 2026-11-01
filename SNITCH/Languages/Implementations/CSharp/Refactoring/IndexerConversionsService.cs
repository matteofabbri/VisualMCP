using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Function to indexer" and "Indexer to function" (Function2Indexer / Indexer2Function).
/// A value method becomes <c>T this[p1, ..., pN] { get; }</c>; a void method whose last parameter is
/// named <c>value</c> becomes the setter. The reverse direction produces <c>Get(...)</c>/<c>Set(..., value)</c>.
/// Call sites must be in the same file: an indexer access has no name token to find in other documents.
/// </summary>
public interface IIndexerConversionsService
{
    Task<object> FunctionToIndexer(
        string filePath,
        string methodName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);

    Task<object> IndexerToFunction(
        string filePath,
        string? containingType = null,
        string? getterName = null,
        string? setterName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class IndexerConversionsService : IIndexerConversionsService
{
    private readonly IRoslynWorkspace _workspace;

    public IndexerConversionsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FunctionToIndexer(
        string filePath,
        string methodName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "A method name is required." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.FunctionToIndexerAsync(
                _workspace, filePath, methodName, containingType, dryRun, cancellationToken);

        var prepared = await PrepareAsync(filePath, cancellationToken);
        if (prepared.Error is not null) return prepared.Error;

        var (solution, document, root, model) = (prepared.Solution!, prepared.Document!, prepared.Root!, prepared.Model!);

        var candidates = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method, cancellationToken)?.ContainingType?.Name,
                                           containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return new { error = $"No method '{methodName}' in the file." };
        if (candidates.Count > 1) return new { error = $"'{methodName}' has overloads; convert one at a time." };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var refusal = RefuseMethod(method);
        if (refusal is not null) return new { error = refusal };
        if (declaration.Body is null && declaration.ExpressionBody is null)
            return new { error = $"'{methodName}' has no body." };

        var parameters = declaration.ParameterList.Parameters;
        var isSetter = method.ReturnsVoid;

        if (isSetter)
        {
            if (parameters.Count == 0 || parameters[^1].Identifier.ValueText != "value")
                return new { error = "A void method becomes an indexer setter only when its last parameter is named 'value'." };
        }
        else if (parameters.Count == 0)
        {
            return new { error = "A method with no parameters cannot become an indexer; use function_to_property." };
        }

        var indexParameters = isSetter ? parameters.Take(parameters.Count - 1).ToList() : parameters.ToList();
        var typeSyntax = isSetter ? parameters[^1].Type ?? ParseTypeName("object") : declaration.ReturnType;

        // ── Usages must be plain calls in this file ──────────────────────────────────────
        var invocations = new List<InvocationExpressionSyntax>();
        var unsupported = new List<string>();

        foreach (var reference in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (declaration.Span.Contains(location.Location.SourceSpan)) continue;
                if (location.Document.Id != document.Id)
                    return new
                    {
                        error = $"'{methodName}' is called from {Path.GetFileName(location.Document.FilePath)}; " +
                                "indexer call sites must be in the same file as the declaration.",
                    };

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                if (invocation is null || !invocation.Expression.DescendantNodesAndSelf().Contains(node))
                {
                    unsupported.Add($"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    continue;
                }

                invocations.Add(invocation);
            }
        }

        if (unsupported.Count > 0)
            return new { error = $"{unsupported.Count} use(s) are not plain calls.", uses = unsupported };

        if (invocations.Any(invocation => declaration.Span.Contains(invocation.Span)))
            return new { error = $"'{methodName}' calls itself; the recursive call would keep the old name." };

        // Rewrite the calls FIRST, then swap the declaration for the indexer. Doing it the other way
        // round shifts the call sites and they are no longer where the original spans say they are.
        var annotation = new SyntaxAnnotation("function-to-indexer");
        var annotated = root.ReplaceNode(declaration, declaration.WithAdditionalAnnotations(annotation));
        var withCalls = ApplyCalls(annotated, invocations, isSetter);

        var annotatedMethod = withCalls.GetAnnotatedNodes(annotation).OfType<MethodDeclarationSyntax>().SingleOrDefault();
        if (annotatedMethod?.Parent is not TypeDeclarationSyntax owner)
            return new { error = "The method is not a member of a type declaration." };

        var accessor = isSetter
            ? AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            : AccessorDeclaration(SyntaxKind.GetAccessorDeclaration);

        accessor = annotatedMethod.ExpressionBody is not null
            ? accessor.WithExpressionBody(annotatedMethod.ExpressionBody).WithSemicolonToken(annotatedMethod.SemicolonToken)
            : accessor.WithBody(annotatedMethod.Body!);

        var indexer = IndexerDeclaration(typeSyntax.WithoutTrivia().WithTrailingTrivia(Space))
            .WithAttributeLists(annotatedMethod.AttributeLists)
            .WithModifiers(annotatedMethod.Modifiers)
            .WithThisKeyword(Token(SyntaxKind.ThisKeyword))
            .WithParameterList(BracketedParameterList(SeparatedList(indexParameters)))
            .WithAccessorList(AccessorList(SingletonList(accessor)))
            .WithLeadingTrivia(annotatedMethod.GetLeadingTrivia())
            .WithTrailingTrivia(annotatedMethod.GetTrailingTrivia());

        var edited = withCalls.ReplaceNode(owner, owner.WithMembers(List(owner.Members.Select(member =>
            member == annotatedMethod ? (MemberDeclarationSyntax)indexer : member))));
        var newText = edited.ToFullString();

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, solution.WithDocumentText(document.Id, SourceText.From(newText)),
            new[] { document.Project.Id }, cancellationToken);

        return await FinishAsync(document, root, newText, gate, "function_to_indexer", dryRun, cancellationToken,
            new { method = method.ToDisplayString(), callSites = invocations.Count });
    }

    public async Task<object> IndexerToFunction(
        string filePath,
        string? containingType = null,
        string? getterName = null,
        string? setterName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.IndexerToFunctionAsync(
                _workspace, filePath, containingType, getterName, setterName, dryRun, cancellationToken);

        var prepared = await PrepareAsync(filePath, cancellationToken);
        if (prepared.Error is not null) return prepared.Error;

        var (solution, document, root, model) = (prepared.Solution!, prepared.Document!, prepared.Root!, prepared.Model!);

        var indexers = root.DescendantNodes().OfType<IndexerDeclarationSyntax>()
            .Where(candidate => containingType is null ||
                                string.Equals(model.GetDeclaredSymbol(candidate, cancellationToken)?.ContainingType?.Name,
                                              containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (indexers.Count == 0) return new { error = "No indexer in the file." };
        if (indexers.Count > 1) return new { error = "The file declares several indexers; this tool converts one at a time (pass containingType)." };

        var indexer = indexers[0];
        if (model.GetDeclaredSymbol(indexer, cancellationToken) is not IPropertySymbol symbol)
            return new { error = "The indexer did not resolve." };

        var getAccessor = indexer.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = indexer.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        if (getAccessor is null && setAccessor is null) return new { error = "The indexer has no accessor to convert." };

        var getter = getAccessor is null ? null : getterName ?? "Get";
        var setter = setAccessor is null ? null : setterName ?? "Set";

        if (indexer.Parent is not TypeDeclarationSyntax owner)
            return new { error = "The indexer is not a member of a type declaration." };

        var ownerSymbol = model.GetDeclaredSymbol(owner, cancellationToken);
        foreach (var name in new[] { getter, setter }.Where(name => name is not null))
        {
            if (!SyntaxFacts.IsValidIdentifier(name!)) return new { error = $"'{name}' is not a valid method name." };
            if (ownerSymbol?.GetMembers(name!).Length > 0)
                return new { error = $"'{ownerSymbol.Name}' already has a member named '{name}'." };
        }

        // ── Usages: a scan of this file, because an element access has no name token ─────
        var accesses = root.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Where(access => IsIndexerUse(model, access, symbol, cancellationToken) ||
                             IsWriteOnTheOwningType(model, access, symbol, cancellationToken))
            .ToList();

        foreach (var project in solution.Projects)
            foreach (var candidate in project.Documents)
            {
                if (candidate.Id == document.Id) continue;

                var candidateRoot = await candidate.GetSyntaxRootAsync(cancellationToken);
                var candidateModel = await candidate.GetSemanticModelAsync(cancellationToken);
                if (candidateRoot is null || candidateModel is null) continue;

                var foreign = candidateRoot.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                    .Any(access => SymbolEqualityComparer.Default.Equals(candidateModel.GetSymbolInfo(access, cancellationToken).Symbol, symbol));
                if (foreign)
                    return new
                    {
                        error = $"The indexer is also used in {Path.GetFileName(candidate.FilePath)}; " +
                                "indexer call sites must be in the same file as the declaration.",
                    };
            }

        var methods = new List<MethodDeclarationSyntax>();
        var indexParameters = indexer.ParameterList.Parameters;
        var parameterText = string.Join(", ", indexParameters.Select(parameter => parameter.WithoutTrivia().ToString()));

        // Built from source text: the hand-assembled header kept gluing the name to the parameter list
        // (`int Get(int index)` came out as a broken property), and text parsing is deterministic.
        if (getAccessor is not null && getter is not null)
        {
            var source = $"{ModifiersFor(indexer, getAccessor)} {indexer.Type.WithoutTrivia()} {getter}({parameterText})" +
                         BodyText(getAccessor);
            if (ParseMemberDeclaration(source) is MethodDeclarationSyntax getterMethod) methods.Add(getterMethod);
        }

        if (setAccessor is not null && setter is not null)
        {
            var source = $"{ModifiersFor(indexer, setAccessor)} void {setter}({parameterText}, {indexer.Type.WithoutTrivia()} value)" +
                         BodyText(setAccessor);
            if (ParseMemberDeclaration(source) is MethodDeclarationSyntax setterMethod) methods.Add(setterMethod);
        }

        if (methods.Count == 0) return new { error = "Could not build the replacement methods." };

        // Same ordering as the other direction: rewrite the element accesses while their nodes are
        // still the ones the semantic model produced, then replace the declaration.
        var annotation = new SyntaxAnnotation("indexer-to-function");
        var annotated = root.ReplaceNode(indexer, indexer.WithAdditionalAnnotations(annotation));
        var withCalls = ApplyElementAccesses(annotated, accesses, getter, setter);

        var annotatedIndexer = withCalls.GetAnnotatedNodes(annotation).OfType<IndexerDeclarationSyntax>().SingleOrDefault();
        if (annotatedIndexer?.Parent is not TypeDeclarationSyntax editedOwner)
            return new { error = "The indexer is not a member of a type declaration." };

        var members = new List<MemberDeclarationSyntax>();
        var indentation = IndentationOf(annotatedIndexer);
        foreach (var member in editedOwner.Members)
        {
            if (member != annotatedIndexer) { members.Add(member); continue; }

            var trailing = methods.Count > 1
                ? TriviaList(ElasticCarriageReturnLineFeed)
                : annotatedIndexer.GetTrailingTrivia();

            for (var i = 0; i < methods.Count; i++)
                members.Add(i == 0
                    ? methods[i].WithLeadingTrivia(annotatedIndexer.GetLeadingTrivia()).WithTrailingTrivia(trailing)
                    : methods[i].WithLeadingTrivia(Whitespace(indentation)).WithTrailingTrivia(annotatedIndexer.GetTrailingTrivia()));
        }

        var edited = withCalls.ReplaceNode(editedOwner, editedOwner.WithMembers(List(members)));
        var newText = edited.ToFullString();

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, solution.WithDocumentText(document.Id, SourceText.From(newText)),
            new[] { document.Project.Id }, cancellationToken);

        return await FinishAsync(document, root, newText, gate, "indexer_to_function", dryRun, cancellationToken,
            new { getter, setter, callSites = accesses.Count });
    }

    private static SyntaxNode ApplyCalls(SyntaxNode root, List<InvocationExpressionSyntax> invocations, bool isSetter)
    {
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var invocation in invocations)
        {
            var current = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Span == invocation.Span);
            if (current is null) continue;

            var receiver = current.Expression is MemberAccessExpressionSyntax access ? access.Expression : current.Expression;
            var arguments = current.ArgumentList.Arguments;

            if (isSetter)
            {
                if (current.Parent is not ExpressionStatementSyntax statement || arguments.Count == 0) continue;

                var value = arguments[^1].Expression.WithoutTrivia();
                var indexArguments = arguments.Take(arguments.Count - 1).Select(argument => Argument(argument.Expression.WithoutTrivia()));

                replacements[statement] = ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        ElementAccessExpression(receiver.WithoutTrivia(),
                            BracketedArgumentList(SeparatedList(indexArguments))),
                        value))
                    .WithTriviaFrom(statement);
            }
            else
            {
                replacements[current] = ElementAccessExpression(receiver.WithoutTrivia(),
                        BracketedArgumentList(SeparatedList(arguments.Select(argument => Argument(argument.Expression.WithoutTrivia())))))
                    .WithTriviaFrom(current);
            }
        }

        return replacements.Count == 0
            ? root
            : root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
    }

    private static SyntaxNode ApplyElementAccesses(
        SyntaxNode root, List<ElementAccessExpressionSyntax> accesses, string? getter, string? setter)
    {
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var access in accesses)
        {
            var current = root.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Span == access.Span);
            if (current is null) continue;

            var indexArguments = current.ArgumentList.Arguments
                .Select(argument => Argument(argument.Expression.WithoutTrivia())).ToList();

            if (current.Parent is AssignmentExpressionSyntax assignment &&
                assignment.Left == current && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                if (setter is null) continue;

                replacements[assignment] = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            current.Expression.WithoutTrivia(), IdentifierName(setter)),
                        ArgumentList(SeparatedList(indexArguments.Append(Argument(assignment.Right.WithoutTrivia())))))
                    .WithTriviaFrom(assignment);
            }
            else
            {
                if (getter is null) continue;

                replacements[current] = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            current.Expression.WithoutTrivia(), IdentifierName(getter)),
                        ArgumentList(SeparatedList(indexArguments)))
                    .WithTriviaFrom(current);
            }
        }

        return replacements.Count == 0
            ? root
            : root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
    }

    private async Task<object> FinishAsync(
        Document document, SyntaxNode root, string newText, CompilerGate gate,
        string refactoring, bool dryRun, CancellationToken cancellationToken, object details)
    {
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the conversion");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = refactoring,
            details,
            file = document.FilePath,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(root.ToFullString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private async Task<(Solution? Solution, Document? Document, SyntaxNode? Root, SemanticModel? Model, object? Error)>
        PrepareAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return (null, null, null, null, new { error = "A file path is required." });

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return (null, null, null, null, new { error = "No C# solution could be auto-located." });

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return (null, null, null, null, new { error = $"Invalid path '{filePath}': {ex.Message}" }); }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return (null, null, null, null, new { error = $"{path} is not a document of the loaded solution." });

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
            return (null, null, null, null, new { error = "Could not read the syntax tree." });

        return (solution, document, root, model, null);
    }

    private static string? RefuseMethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary) return $"'{method.Name}' is a {method.MethodKind}.";
        if (method.IsStatic) return "An indexer is an instance member; a static method cannot become one.";
        if (method.IsGenericMethod) return "A generic method cannot become an indexer.";
        if (method.IsAbstract || method.IsVirtual || method.IsOverride) return $"'{method.Name}' is abstract/virtual/override.";
        if (method.IsAsync) return $"'{method.Name}' is async.";
        if (method.ExplicitInterfaceImplementations.Length > 0) return $"'{method.Name}' implements an interface explicitly.";
        return null;
    }

    /// <summary>
    /// True for a read and for a write: on `x[i] = v` the semantic model can report the accessor in the
    /// candidates instead of the indexer itself, so both slots are checked.
    /// </summary>
    private static bool IsIndexerUse(SemanticModel model, ElementAccessExpressionSyntax access, IPropertySymbol symbol,
        CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(access, cancellationToken);
        if (SymbolEqualityComparer.Default.Equals(info.Symbol, symbol)) return true;
        if (info.CandidateSymbols.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, symbol))) return true;

        if (access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
        {
            var assigned = model.GetSymbolInfo(assignment, cancellationToken);
            if (SymbolEqualityComparer.Default.Equals(assigned.Symbol, symbol)) return true;
            if (assigned.CandidateSymbols.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, symbol))) return true;
            if (assigned.Symbol is IMethodSymbol method &&
                SymbolEqualityComparer.Default.Equals(method.AssociatedSymbol, symbol)) return true;
        }

        return false;
    }

    /// <summary>
    /// A write target: on `x[i] = v` the model reports the setter, not the indexer, and not always in a
    /// form that compares equal, so an element access assigned on the same containing type counts too.
    /// </summary>
    private static bool IsWriteOnTheOwningType(SemanticModel model, ElementAccessExpressionSyntax access,
        IPropertySymbol symbol, CancellationToken cancellationToken) =>
        access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access &&
        SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(access.Expression, cancellationToken).Type, symbol.ContainingType);

    private static string ModifiersFor(IndexerDeclarationSyntax indexer, AccessorDeclarationSyntax accessor)
    {
        var modifiers = accessor.Modifiers.Count > 0 ? accessor.Modifiers : indexer.Modifiers;
        return string.Join(" ", modifiers.Select(modifier => modifier.Text));
    }

    private static string BodyText(AccessorDeclarationSyntax accessor) =>
        accessor.ExpressionBody is not null
            ? " => " + accessor.ExpressionBody.Expression.WithoutTrivia() + ";"
            : accessor.Body?.ToFullString() ?? " { }";

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }
}
