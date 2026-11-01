using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Generify": a concrete type used inside a method or a type declaration becomes a type
/// parameter, and the call sites or references supply the concrete type.
/// </summary>
/// <remarks>
/// Only the positions where a type parameter may legally appear are abstracted: a static member access
/// (<c>int.Parse</c>), an attribute, a <c>nameof</c> argument and a <c>const</c> declaration keep the
/// concrete type. A method whose new type parameter cannot be inferred from its arguments gets the
/// concrete type as an explicit type argument at every call site; a type gets it at every reference.
/// Everything is gated on the compiler, so a body that needs a constraint refuses until one is passed.
/// </remarks>
public interface IGenerifyService
{
    Task<object> Generify(
        string filePath,
        string? methodName = null,
        string? typeName = null,
        string? containingType = null,
        string[]? usages = null,
        Dictionary<string, string>? typeParameterNames = null,
        Dictionary<string, string>? constraints = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

public sealed class GenerifyService : IGenerifyService
{
    private readonly IRoslynWorkspace _workspace;

    public GenerifyService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> Generify(
        string filePath,
        string? methodName = null,
        string? typeName = null,
        string? containingType = null,
        string[]? usages = null,
        Dictionary<string, string>? typeParameterNames = null,
        Dictionary<string, string>? constraints = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(methodName) == string.IsNullOrWhiteSpace(typeName))
            return new { error = "Pass exactly one of methodName or typeName." };

        var usageTexts = (usages ?? []).Where(usage => !string.IsNullOrWhiteSpace(usage)).Distinct(StringComparer.Ordinal).ToList();
        if (usageTexts.Count == 0) return new { error = "Name at least one concrete type in 'usages'." };

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
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.GenerifyAsync(
                _workspace, filePath, methodName, typeName, containingType, usages, typeParameterNames, constraints,
                dryRun, cancellationToken);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var resolved = new List<(string Usage, ITypeSymbol Type, string ParameterName)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        // Bind at a position inside the file (not at offset 0), so the file's usings and namespace
        // are in scope for the usage text.
        var bindingPosition = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.SpanStart ?? 0;

        foreach (var usage in usageTexts)
        {
            var parsed = ParseTypeName(usage);
            var info = model.GetSpeculativeTypeInfo(bindingPosition, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace);
            if (info.Type is null)
                return new { error = $"'{usage}' does not resolve to a type in {Path.GetFileName(path)}." };

            var name = typeParameterNames is not null && typeParameterNames.TryGetValue(usage, out var configured)
                ? configured
                : null;

            name ??= index == 0 ? "T" : "T" + index;
            while (!taken.Add(name)) name += index;

            resolved.Add((usage, info.Type, name));
            index++;
        }

        foreach (var (_, _, name) in resolved)
        {
            if (!SyntaxFacts.IsValidIdentifier(name))
                return new { error = $"'{name}' is not a valid type parameter name." };
        }

        return string.IsNullOrWhiteSpace(methodName)
            ? await GenerifyTypeAsync(document, root, model, typeName!, containingType, resolved, constraints, dryRun, cancellationToken)
            : await GenerifyMethodAsync(document, root, model, methodName!, containingType, resolved, constraints, dryRun, cancellationToken);
    }

    // ── method mode ────────────────────────────────────────────────────────────────

    private async Task<object> GenerifyMethodAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        string methodName,
        string? containingType,
        IReadOnlyList<(string Usage, ITypeSymbol Type, string ParameterName)> usages,
        Dictionary<string, string>? constraints,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var declarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText.Equals(methodName, StringComparison.Ordinal))
            .Where(node => containingType is null ||
                           node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
                               .Equals(containingType, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (declarations.Count == 0) return new { error = $"No method '{methodName}' in {Path.GetFileName(document.FilePath)}." };
        if (declarations.Count > 1)
            return new { error = $"Several methods named '{methodName}' in the file; generify needs the single declaration." };

        var declaration = declarations[0];
        if (declaration.Body is null && declaration.ExpressionBody is null)
            return new { error = "'generify' works on a method with a body." };

        var symbol = model.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol;
        if (symbol is null) return new { error = $"'{methodName}' did not resolve to a method." };

        var existingTypeParameters = symbol.TypeParameters.Length;
        var abstracted = new List<(string Usage, string ParameterName)>();
        var tooFew = new List<string>();

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var (usage, type, parameterName) in usages)
        {
            var candidates = AbstractCandidates(declaration, model, type, cancellationToken);
            if (candidates.Count == 0)
            {
                tooFew.Add(usage);
                continue;
            }

            foreach (var candidate in candidates)
                replacements[candidate] = IdentifierName(parameterName).WithTriviaFrom(candidate);

            abstracted.Add((usage, parameterName));
        }

        if (tooFew.Count > 0)
            return new
            {
                error = $"No abstractable occurrence of {string.Join(", ", tooFew.Select(usage => $"'{usage}'"))} in the method: " +
                        "the type appears only where a type parameter cannot (static member access, attribute, nameof, const).",
            };

        var rewritten = declaration.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);

        var typeParameterNodes = rewritten.TypeParameterList?.Parameters.ToList() ?? [];
        typeParameterNodes.AddRange(abstracted.Select(entry => TypeParameter(entry.ParameterName)));
        var typeParameters = TypeParameterList(SeparatedList(typeParameterNodes, Commas(typeParameterNodes.Count)));

        rewritten = rewritten.WithTypeParameterList(typeParameters);
        rewritten = AddConstraints(rewritten, abstracted, constraints);

        // A type parameter that appears in no parameter cannot be inferred: every call site gets the
        // concrete type as an explicit type argument.
        var inferred = abstracted
            .Where(entry => rewritten.ParameterList.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(node => node.Identifier.ValueText == entry.ParameterName))
            .Select(entry => entry.ParameterName)
            .ToHashSet(StringComparer.Ordinal);

        var explicitTypeArguments = abstracted
            .Where(entry => !inferred.Contains(entry.ParameterName))
            .Select(entry => entry.Usage)
            .ToList();

        if (explicitTypeArguments.Count > 0 && existingTypeParameters > 0)
            return new
            {
                error = "The new type parameter cannot be inferred from the parameters, and the method already has type " +
                        $"parameter(s) {string.Join(", ", symbol.TypeParameters.Select(parameter => parameter.Name))}: an explicit " +
                        "type argument list must spell all of them. Make the type appear in a parameter instead.",
            };

        var filesToWrite = new Dictionary<DocumentId, string>();
        var callSites = 0;

        if (explicitTypeArguments.Count == 0)
        {
            filesToWrite[document.Id] = root.ReplaceNode(declaration, rewritten).ToFullString();
        }
        else
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, cancellationToken);
            var locations = references
                .SelectMany(reference => reference.Locations)
                .Where(location => location.Location.IsInSource)
                .ToList();

            // A recursive call lives inside the declaration: it would need the explicit argument too,
            // and the declaration is rebuilt from the pre-rewrite tree, so refuse rather than break it.
            if (locations.Any(location =>
                    location.Document.Id == document.Id && declaration.Span.Contains(location.Location.SourceSpan)))
                return new
                {
                    error = "The method calls itself and the new type parameter cannot be inferred from its parameters: " +
                            "the recursive call cannot be given an explicit type argument by this refactoring. " +
                            "Make the type appear in a parameter instead.",
                };

            // Call sites are annotated in their own trees BEFORE the declaration changes: inserting the
            // type parameter list shifts every span after it.
            var roots = new Dictionary<DocumentId, SyntaxNode>();
            var annotations = new List<(DocumentId Document, SyntaxAnnotation Annotation)>();

            foreach (var group in locations.GroupBy(location => location.Document))
            {
                var targetRoot = group.Key.Id == document.Id ? root : await group.Key.GetSyntaxRootAsync(cancellationToken);
                if (targetRoot is null) continue;

                var nodes = group
                    .Select(location => targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true))
                    .OfType<SimpleNameSyntax>()
                    .Where(node => node.Identifier.ValueText == methodName)
                    .Where(node => !IsInsideNameOf(node))
                    .Distinct()
                    .ToList();

                if (nodes.Count == 0)
                {
                    roots[group.Key.Id] = targetRoot;
                    continue;
                }

                var byNode = nodes.ToDictionary(node => node, _ => new SyntaxAnnotation("generify_call"));
                foreach (var (_, annotation) in byNode) annotations.Add((group.Key.Id, annotation));

                roots[group.Key.Id] = targetRoot.ReplaceNodes(byNode.Keys, (original, _) =>
                    original.WithAdditionalAnnotations(byNode[original]));
            }

            if (!roots.ContainsKey(document.Id)) roots[document.Id] = root;

            var sourceRoot = roots[document.Id];
            var sourceDeclaration = sourceRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .First(node => node.SpanStart == declaration.SpanStart && node.Identifier.ValueText == methodName);
            roots[document.Id] = sourceRoot.ReplaceNode(sourceDeclaration, rewritten);

            foreach (var (documentId, annotation) in annotations)
            {
                var targetRoot = roots[documentId];
                var name = targetRoot.GetAnnotatedNodes(annotation).OfType<SimpleNameSyntax>().SingleOrDefault();
                if (name is null) continue;

                var replacement = GenericName(name.Identifier.WithoutTrivia(),
                        TypeArgumentList(SeparatedList(
                            explicitTypeArguments.Select(argument => ParseTypeName(argument)),
                            Commas(explicitTypeArguments.Count))))
                    .WithTriviaFrom(name);

                roots[documentId] = targetRoot.ReplaceNode(name, replacement);
                callSites++;
            }

            foreach (var (documentId, targetRoot) in roots)
            {
                var original = await document.Project.Solution.GetDocument(documentId)!.GetTextAsync(cancellationToken);
                var newText = targetRoot.ToFullString();
                if (!string.Equals(original.ToString(), newText, StringComparison.Ordinal))
                    filesToWrite[documentId] = newText;
            }
        }

        var gate = await GateAsync(document.Project.Solution, filesToWrite, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "generify");

        return await FinishAsync("generify_method", document, filesToWrite, dryRun,
            new
            {
                method = symbol.ToDisplayString(),
                typeParameters = abstracted.Select(entry => new { usage = entry.Usage, name = entry.ParameterName }).ToList(),
                inferred = abstracted.Where(entry => inferred.Contains(entry.ParameterName)).Select(entry => entry.ParameterName).ToList(),
                explicitTypeArguments,
                callSitesUpdated = callSites,
            },
            gate, cancellationToken);
    }

    // ── type mode ──────────────────────────────────────────────────────────────────

    private async Task<object> GenerifyTypeAsync(
        Document document,
        SyntaxNode root,
        SemanticModel model,
        string typeName,
        string? containingType,
        IReadOnlyList<(string Usage, ITypeSymbol Type, string ParameterName)> usages,
        Dictionary<string, string>? constraints,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var declarations = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText.Equals(typeName, StringComparison.Ordinal))
            .Where(node => containingType is null ||
                           node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
                               .Equals(containingType, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        if (declarations.Count == 0) return new { error = $"No type '{typeName}' in {Path.GetFileName(document.FilePath)}." };
        if (declarations.Count > 1) return new { error = $"Several types named '{typeName}' in the file." };

        var declaration = declarations[0] as TypeDeclarationSyntax;
        if (declaration is null) return new { error = $"'{typeName}' is not a class, struct, interface or record." };

        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        if (symbol.DeclaringSyntaxReferences.Length > 1)
            return new { error = $"'{typeName}' is partial: every declaration would need the type parameters. Make it one declaration first." };

        var abstracted = new List<(string Usage, string ParameterName)>();
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var (usage, type, parameterName) in usages)
        {
            var candidates = AbstractCandidates(declaration, model, type, cancellationToken);
            if (candidates.Count == 0)
                return new { error = $"No abstractable occurrence of '{usage}' in '{typeName}'." };

            foreach (var candidate in candidates)
                replacements[candidate] = IdentifierName(parameterName).WithTriviaFrom(candidate);

            abstracted.Add((usage, parameterName));
        }

        var typeParameterNodes = declaration.TypeParameterList?.Parameters.ToList() ?? [];
        typeParameterNodes.AddRange(abstracted.Select(entry => TypeParameter(entry.ParameterName)));
        var typeParameters = TypeParameterList(SeparatedList(typeParameterNodes, Commas(typeParameterNodes.Count)));

        // ── Every reference to the type spells the concrete arguments ────────────────────
        // References are resolved against the ORIGINAL tree and the ORIGINAL model; the type
        // parameter list is applied afterwards, on the rebuilt declaration.
        var filesToWrite = new Dictionary<DocumentId, string>();
        var referenceCount = 0;
        var argumentList = SeparatedList(abstracted.Select(entry => ParseTypeName(entry.Usage)), Commas(abstracted.Count));

        foreach (var target in document.Project.Solution.Projects.SelectMany(project => project.Documents))
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var replacementsForDocument = new Dictionary<SyntaxNode, SyntaxNode>();
            if (target.Id == document.Id)
            {
                foreach (var (node, replacement) in replacements) replacementsForDocument[node] = replacement;
            }

            var candidates = targetRoot.DescendantNodes().OfType<NameSyntax>()
                .Where(node => SymbolEqualityComparer.Default.Equals(
                    targetModel.GetSymbolInfo(node, cancellationToken).Symbol, symbol))
                .ToList();

            var candidateSet = new HashSet<SyntaxNode>(candidates);
            var outermost = candidates.Where(node => !node.Ancestors().Any(candidateSet.Contains)).ToList();

            foreach (var node in outermost)
                replacementsForDocument[node] = GenericName(Identifier(typeName).WithoutTrivia(), TypeArgumentList(argumentList))
                    .WithTriviaFrom(node);

            if (replacementsForDocument.Count == 0) continue;

            var replaced = targetRoot.ReplaceNodes(replacementsForDocument.Keys,
                (original, _) => replacementsForDocument[original]);

            if (target.Id == document.Id)
            {
                var rebuilt = replaced.DescendantNodes().OfType<TypeDeclarationSyntax>()
                    .First(node => node.Identifier.ValueText == typeName && node.RawKind == declaration.RawKind);

                // The identifier's trailing trivia (often the newline before the opening brace) belongs
                // after the list, not between the name and '<'.
                var trailing = rebuilt.Identifier.TrailingTrivia;
                var withTypeParameters = rebuilt
                    .WithIdentifier(rebuilt.Identifier.WithTrailingTrivia())
                    .WithTypeParameterList(typeParameters.WithTrailingTrivia(trailing));
                withTypeParameters = AddConstraints(withTypeParameters, abstracted, constraints);
                replaced = replaced.ReplaceNode(rebuilt, withTypeParameters);
            }

            referenceCount += outermost.Count;
            filesToWrite[target.Id] = replaced.ToFullString();
        }

        var gate = await GateAsync(document.Project.Solution, filesToWrite, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "generify");

        return await FinishAsync("generify_type", document, filesToWrite, dryRun,
            new
            {
                type = symbol.ToDisplayString(),
                typeParameters = abstracted.Select(entry => new { usage = entry.Usage, name = entry.ParameterName }).ToList(),
                referencesUpdated = referenceCount,
            },
            gate, cancellationToken);
    }

    // ── shared ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The occurrences of <paramref name="usage"/> inside the declaration where a type parameter may
    /// stand. Static member access, attributes, nameof and const declarations are skipped because the
    /// replacement there would not compile.
    /// </summary>
    private static List<TypeSyntax> AbstractCandidates(
        SyntaxNode declaration, SemanticModel model, ITypeSymbol usage, CancellationToken cancellationToken)
    {
        var candidates = new List<TypeSyntax>();

        foreach (var node in declaration.DescendantNodes().OfType<TypeSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(node, cancellationToken).Type, usage)) continue;
            if (IsDisallowed(node, declaration)) continue;

            candidates.Add(node);
        }

        return candidates;
    }

    private static bool IsDisallowed(SyntaxNode node, SyntaxNode declaration)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor == declaration) break;

            switch (ancestor)
            {
                case AttributeSyntax:
                    return true;
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }:
                    return true;
                case MemberAccessExpressionSyntax access when access.Expression.Span.Contains(node.Span):
                    return true;
                case LocalDeclarationStatementSyntax local when local.Modifiers.Any(SyntaxKind.ConstKeyword):
                case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.ConstKeyword):
                    return true;
            }
        }

        return false;
    }

    private static TDeclaration AddConstraints<TDeclaration>(
        TDeclaration declaration,
        IReadOnlyList<(string Usage, string ParameterName)> abstracted,
        Dictionary<string, string>? constraints)
        where TDeclaration : MemberDeclarationSyntax
    {
        if (constraints is null || constraints.Count == 0) return declaration;

        var clauses = declaration switch
        {
            MethodDeclarationSyntax method => method.ConstraintClauses,
            TypeDeclarationSyntax type => type.ConstraintClauses,
            _ => default,
        };

        foreach (var (usage, parameterName) in abstracted)
        {
            var text = constraints.TryGetValue(parameterName, out var configured)
                ? configured
                : constraints.TryGetValue(usage, out var byUsage) ? byUsage : null;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var parsed = (MethodDeclarationSyntax)ParseMemberDeclaration(
                $"void __Generify<{parameterName}>() where {parameterName} : {text} {{ }}")!;
            clauses = clauses.Add(parsed.ConstraintClauses[0]);
        }

        return declaration switch
        {
            MethodDeclarationSyntax method => (TDeclaration)(MemberDeclarationSyntax)method.WithConstraintClauses(clauses),
            TypeDeclarationSyntax type => (TDeclaration)(MemberDeclarationSyntax)type.WithConstraintClauses(clauses),
            _ => declaration,
        };
    }

    private static async Task<CompilerGate> GateAsync(
        Solution solution, Dictionary<DocumentId, string> filesToWrite, CancellationToken cancellationToken)
    {
        var projected = solution;
        var projectIds = new HashSet<ProjectId>();

        foreach (var (documentId, text) in filesToWrite)
        {
            var document = solution.GetDocument(documentId);
            if (document is null) continue;

            projected = projected.WithDocumentText(documentId, SourceText.From(text));
            projectIds.Add(document.Project.Id);
        }

        return await RefactoringCompilerGate.CompareAsync(solution, projected, projectIds, cancellationToken);
    }

    private async Task<object> FinishAsync(
        string refactoring,
        Document document,
        Dictionary<DocumentId, string> filesToWrite,
        bool dryRun,
        object details,
        CompilerGate gate,
        CancellationToken cancellationToken)
    {
        if (!dryRun)
        {
            foreach (var (documentId, text) in filesToWrite)
            {
                var target = document.Project.Solution.GetDocument(documentId);
                if (target is not null)
                    await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, target, text, cancellationToken);
            }
        }

        var files = new List<object>();
        foreach (var (documentId, text) in filesToWrite)
        {
            var target = document.Project.Solution.GetDocument(documentId);
            var oldText = target is null ? string.Empty : (await target.GetTextAsync(cancellationToken)).ToString();
            files.Add(new { file = target?.FilePath, preview = RefactoringFileWriter.Diff(oldText, text) });
        }

        return new
        {
            @refactoring = refactoring,
            file = document.FilePath,
            details,
            filesChanged = filesToWrite.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            files,
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static IEnumerable<SyntaxToken> Commas(int count) =>
        Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, count - 1));

    private static bool IsInsideNameOf(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });
}
