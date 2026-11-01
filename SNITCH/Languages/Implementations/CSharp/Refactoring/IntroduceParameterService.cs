using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Introduce parameter" (the deterministic half): an expression whose value cannot depend on
/// where it is evaluated — a literal, a constant, arithmetic on constants, <c>nameof(...)</c>,
/// <c>typeof(...)</c>, <c>default(T)</c> — becomes a parameter, and every call site passes that same
/// expression, so behaviour is preserved.
/// </summary>
public interface IIntroduceParameterService
{
    Task<object> IntroduceParameter(
        string filePath,
        int line,
        string expressionText,
        string parameterName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class IntroduceParameterService : IIntroduceParameterService
{
    private readonly IRoslynWorkspace _workspace;

    public IntroduceParameterService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> IntroduceParameter(
        string filePath,
        int line,
        string expressionText,
        string parameterName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };
        if (string.IsNullOrWhiteSpace(expressionText)) return new { error = "The exact literal text is required." };
        if (string.IsNullOrWhiteSpace(parameterName) || !SyntaxFacts.IsValidIdentifier(parameterName))
            return new { error = $"'{parameterName}' is not a valid parameter name." };

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

        if (document.Project.Language == LanguageNames.VisualBasic)
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.IntroduceParameterAsync(
                _workspace, filePath, line, expressionText, parameterName, dryRun, cancellationToken);

        var sourceText = await document.GetTextAsync(cancellationToken);
        if (line > sourceText.Lines.Count) return new { error = $"Line {line} is out of range." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var textLine = sourceText.Lines[line - 1];
        var column = textLine.ToString().IndexOf(expressionText, StringComparison.Ordinal);
        if (column < 0) return new { error = $"'{expressionText}' not found on line {line}." };

        // Any expression counts, not just a literal — but only one whose value is settled by the code
        // alone. Passing it at the call sites is only faithful when it does not read the caller's state.
        var expression = root.DescendantNodes(textLine.Span)
            .OfType<ExpressionSyntax>()
            .Where(candidate => candidate.ToString() == expressionText)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (expression is null)
            return new { error = $"'{expressionText}' is not a single expression on line {line}." };

        var contextBound = expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Select(identifier => model.GetSymbolInfo(identifier, cancellationToken).Symbol)
            .FirstOrDefault(symbol => symbol is ILocalSymbol or IParameterSymbol or IMethodSymbol or IPropertySymbol
                                              or IFieldSymbol { IsConst: false });
        if (contextBound is not null)
            return new
            {
                error = $"'{expressionText}' reads '{contextBound.Name}', whose value depends on where it is evaluated; " +
                        "passing the same text at every call site would change behaviour.",
            };

        var constant = model.GetConstantValue(expression, cancellationToken);
        var isTypeOrDefault = expression is TypeOfExpressionSyntax or DefaultExpressionSyntax ||
                              expression.IsKind(SyntaxKind.DefaultLiteralExpression);
        if (!constant.HasValue && !isTypeOrDefault)
            return new
            {
                error = $"'{expressionText}' is not fixed at compile time, so no call site can pass it without inventing a value. " +
                        "introduce_parameter handles constants, typeof(...) and default(T).",
            };

        var methodDeclaration = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDeclaration is null)
            return new { error = "The expression is not inside a method." };

        // A block and an expression-bodied member are handled the same way: both are one node whose
        // occurrences get replaced, then put back.
        if (methodDeclaration.Body is null && methodDeclaration.ExpressionBody is null)
            return new { error = $"'{methodDeclaration.Identifier.ValueText}' has no body to rewrite." };

        if (model.GetDeclaredSymbol(methodDeclaration, cancellationToken) is not IMethodSymbol method)
            return new { error = "The enclosing method did not resolve." };

        if (method.IsAbstract || method.IsVirtual || method.IsOverride || method.IsExtern)
            return new { error = $"'{method.Name}' is abstract/virtual/override/extern: its signature must not change here." };

        if (method.ExplicitInterfaceImplementations.Length > 0 ||
            (method.ContainingType is { } owner && owner.AllInterfaces
                .SelectMany(reference => reference.GetMembers(method.Name))
                .Any(interfaceMember => SymbolEqualityComparer.Default.Equals(
                    owner.FindImplementationForInterfaceMember(interfaceMember), method))))
        {
            return new { error = $"'{method.Name}' implements an interface member: the signature must stay identical." };
        }

        if (method.Parameters.Any(parameter => parameter.Name == parameterName))
            return new { error = $"'{parameterName}' is already a parameter of '{method.Name}'." };

        var type = model.GetTypeInfo(expression, cancellationToken).Type
                   ?? model.GetTypeInfo(expression, cancellationToken).ConvertedType;
        if (type is null || type.TypeKind == TypeKind.Error) return new { error = "The expression has no resolvable type." };

        // Every use of the method must be a plain call we can extend.
        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        var callSiteDocuments = new Dictionary<DocumentId, Document>();
        var unsupported = new List<string>();
        var callSites = 0;

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id == document.Id && methodDeclaration.Span.Contains(location.Location.SourceSpan)) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation is null)
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{Line(node)} (not a call)");
                    continue;
                }

                if (invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null))
                {
                    unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:{Line(invocation)} (named argument)");
                    continue;
                }

                callSiteDocuments[location.Document.Id] = location.Document;
                callSites++;
            }
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) cannot take the new argument; nothing was written.",
                uses = unsupported.Take(10).ToList(),
            };

        // ── Build every new text first ────────────────────────────────────────────────────
        var typeText = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var parameterNode = ParseParameterList($"{typeText} {parameterName}").Parameters[0]
            .WithLeadingTrivia(Space);

        var argumentText = expressionText;

        var edits = new List<(Document Document, string NewText, string Preview, int Changes)>();

        // 1. The declaring document: append the argument at every call *and* add the parameter.
        //    Both rewrites live in one tree, so the symbol lookups are done on the original root
        //    first and the replacements are then applied by node identity.
        var sameDocumentCallSites = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => !methodDeclaration.Span.Contains(invocation.Span))
            .Where(invocation => SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(invocation, cancellationToken).Symbol, method))
            .ToList();

        var annotation = new SyntaxAnnotation("introduce_parameter");
        var annotatedRoot = root.ReplaceNode(
            methodDeclaration, methodDeclaration.WithAdditionalAnnotations(annotation));

        var annotatedCalls = annotatedRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => sameDocumentCallSites.Any(original => original.Span == invocation.Span))
            .ToList();

        var withCalls = annotatedRoot.ReplaceNodes(
            annotatedCalls, (original, _) => AddArgument(original, argumentText));

        var annotatedMethod = withCalls.GetAnnotatedNodes(annotation).OfType<MethodDeclarationSyntax>().Single();

        // The semantic model belongs to the original tree, so every occurrence is located there and
        // then matched by span in the rewritten one — same offsets, since only annotations were added.
        var occurrenceSpans = ((SyntaxNode?)methodDeclaration.Body ?? methodDeclaration.ExpressionBody)!
            .DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(candidate => IsSameExpression(candidate, expression, model, cancellationToken))
            .Select(candidate => candidate.Span)
            .ToHashSet();

        var annotatedBody = (SyntaxNode?)annotatedMethod.Body ?? annotatedMethod.ExpressionBody;
        var occurrences = annotatedBody!.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(candidate => occurrenceSpans.Contains(candidate.Span))
            .ToList();

        var replacedBody = annotatedBody.ReplaceNodes(occurrences,
            (original, _) => IdentifierName(parameterName).WithTriviaFrom(original));

        var updatedMethod = (annotatedMethod.Body is not null
                ? annotatedMethod.WithBody((BlockSyntax)replacedBody)
                : annotatedMethod.WithExpressionBody((ArrowExpressionClauseSyntax)replacedBody))
            .WithParameterList(annotatedMethod.ParameterList.WithParameters(
                annotatedMethod.ParameterList.Parameters.Add(parameterNode)));

        var declarationRoot = withCalls.ReplaceNode(annotatedMethod, updatedMethod);
        var declarationText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newDeclarationText = declarationRoot.ToFullString();
        edits.Add((document, newDeclarationText,
            RefactoringFileWriter.Diff(declarationText, newDeclarationText),
            occurrences.Count + annotatedCalls.Count));

        // 2. Call sites in other documents pass the same literal.
        foreach (var (documentId, target) in callSiteDocuments)
        {
            if (documentId == document.Id) continue;

            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var invocations = targetRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => SymbolEqualityComparer.Default.Equals(
                    targetModel.GetSymbolInfo(invocation, cancellationToken).Symbol, method))
                .ToList();

            var newRoot = targetRoot.ReplaceNodes(
                invocations, (original, _) => AddArgument(original, argumentText));
            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var newText = newRoot.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            edits.Add((target, newText, RefactoringFileWriter.Diff(oldText, newText), invocations.Count));
        }

        if (!dryRun)
        {
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);
        }

        return new
        {
            @refactoring = "introduce_parameter",
            method = method.ToDisplayString(),
            parameter = $"{type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {parameterName}",
            expression = expressionText,
            passedAtEveryCallSite = expressionText,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            occurrencesReplaced = occurrences.Count,
            callSitesUpdated = callSites,
            filesChanged = edits.Count,
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                changes = edit.Changes,
                preview = edit.Preview,
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>Appends one argument, keeping the comma spacing right for empty and non-empty lists.</summary>
    private static InvocationExpressionSyntax AddArgument(InvocationExpressionSyntax invocation, string argumentText)
    {
        var argument = Argument(ParseExpression(argumentText));
        if (invocation.ArgumentList.Arguments.Count > 0)
            argument = argument.WithLeadingTrivia(Space);

        return invocation.WithArgumentList(invocation.ArgumentList.AddArguments(argument));
    }

    /// <summary>
    /// Whether two nodes denote the same value: same text, same constant value and same type. Text
    /// alone would rewrite a same-spelled expression that means something else.
    /// </summary>
    private static bool IsSameExpression(
        ExpressionSyntax candidate, ExpressionSyntax original, SemanticModel model, CancellationToken cancellationToken)
    {
        if (!string.Equals(candidate.ToString(), original.ToString(), StringComparison.Ordinal)) return false;

        var originalConstant = model.GetConstantValue(original, cancellationToken);
        if (!originalConstant.HasValue) return true; // typeof/default: the text is the whole identity

        var candidateConstant = model.GetConstantValue(candidate, cancellationToken);
        if (!candidateConstant.HasValue || !Equals(candidateConstant.Value, originalConstant.Value)) return false;

        return SymbolEqualityComparer.Default.Equals(
            model.GetTypeInfo(candidate, cancellationToken).Type,
            model.GetTypeInfo(original, cancellationToken).Type);
    }

    private static int Line(SyntaxNode? node) =>
        node is null ? 0 : node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
