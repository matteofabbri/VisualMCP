using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Generify" on a Visual Basic method or type: a concrete type used in the declaration becomes a
/// type parameter (<c>(Of T)</c>), constraints are written in VB's <c>As { … }</c> vocabulary, and a type
/// parameter that cannot be inferred from the parameters is supplied as an explicit
/// <c>(Of Concrete)</c> at every call site or reference. Compiler-gated like the rest of the tail.
/// </summary>
internal static partial class VbRefactoringTail
{
    public static async Task<object> GenerifyAsync(IRoslynWorkspace workspace, string filePath, string? methodName,
        string? typeName, string? containingType, string[]? usages,
        Dictionary<string, string>? typeParameterNames, Dictionary<string, string>? constraints, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(methodName) == string.IsNullOrWhiteSpace(typeName))
            return new { error = "Pass exactly one of methodName or typeName." };

        var usageTexts = (usages ?? []).Where(usage => !string.IsNullOrWhiteSpace(usage))
            .Distinct(StringComparer.Ordinal).ToList();
        if (usageTexts.Count == 0) return new { error = "Name at least one concrete type in 'usages'." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var bindingPosition = root.DescendantNodes().OfType<TypeBlockSyntax>().FirstOrDefault()?.SpanStart ?? 0;
        var resolved = new List<(string Usage, ITypeSymbol Type, string Name)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var usage in usageTexts)
        {
            var parsed = SyntaxFactory.ParseTypeName(usage);
            var info = model.GetSpeculativeTypeInfo(bindingPosition, parsed, SpeculativeBindingOption.BindAsTypeOrNamespace);
            if (info.Type is null)
                return new { error = $"'{usage}' does not resolve to a type in {Path.GetFileName(filePath)}." };

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
            ? await GenerifyTypeAsync(workspace, solution, document, root, model, typeName!, containingType, resolved,
                constraints, dryRun, cancellationToken)
            : await GenerifyMethodAsync(workspace, solution, document, root, model, methodName!, containingType, resolved,
                constraints, dryRun, cancellationToken);
    }

    // ── method mode ──────────────────────────────────────────────────────────────
    private static async Task<object> GenerifyMethodAsync(IRoslynWorkspace workspace, Solution solution,
        Document document, SyntaxNode root, SemanticModel model, string methodName, string? containingType,
        IReadOnlyList<(string Usage, ITypeSymbol Type, string Name)> usages,
        Dictionary<string, string>? constraints, bool dryRun, CancellationToken cancellationToken)
    {
        var declarations = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(method => string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, methodName,
                StringComparison.Ordinal))
            .Where(method => containingType is null ||
                             string.Equals(method.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault()?
                                 .BlockStatement.Identifier.ValueText, containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (declarations.Count == 0)
            return new { error = $"No method '{methodName}' in {Path.GetFileName(document.FilePath)}." };
        if (declarations.Count > 1)
            return new { error = $"Several methods named '{methodName}' in the file; generify needs the single declaration." };

        var method = declarations[0];
        var statement = method.SubOrFunctionStatement;
        if (model.GetDeclaredSymbol(statement, cancellationToken) is not IMethodSymbol symbol)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var existing = statement.TypeParameterList?.Parameters
            .Select(parameter => parameter.Identifier.ValueText).ToList() ?? [];
        if (existing.Count > 0)
            return new
            {
                error = $"'{methodName}' already has type parameter(s) {string.Join(", ", existing)}; the VB path " +
                        "adds the first one. Make the method non-generic first, or edit the declaration by hand.",
            };

        var edits = new Dictionary<DocumentId, List<TextEdit>>();
        var abstracted = new List<(string Usage, string Name, bool Inferable)>();

        foreach (var (usage, type, name) in usages)
        {
            var candidates = AbstractCandidates(method, model, type, cancellationToken);
            if (candidates.Count == 0)
                return new
                {
                    error = $"No abstractable occurrence of '{usage}' in '{methodName}': the type appears only where a " +
                            "type parameter cannot (an attribute, a NameOf argument, a Const declaration or a static " +
                            "member access).",
                };

            foreach (var candidate in candidates)
                AddEdit(edits, document, candidate.Span, name);

            var inferable = candidates.Any(candidate => candidate.Ancestors().OfType<ParameterSyntax>().Any());
            abstracted.Add((usage, name, inferable));
        }

        AddEdit(edits, document, new TextSpan(statement.Identifier.Span.End, 0),
            TypeParameterListText(abstracted.Select(entry => (entry.Name, entry.Usage)).ToList(), constraints));

        var explicitArguments = abstracted.Where(entry => !entry.Inferable).ToList();
        var callSites = 0;

        if (explicitArguments.Count > 0)
        {
            var argumentText = string.Join(", ", explicitArguments.Select(entry => entry.Usage));

            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                {
                    if (method.Span.Contains(location.Location.SourceSpan)) continue;

                    var targetRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                    if (targetRoot is null) continue;

                    var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation is null || !invocation.Span.Contains(location.Location.SourceSpan))
                        return new
                        {
                            error = $"'{methodName}' is used at {Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} in a way that is not " +
                                    "a plain call; an explicit type argument cannot be inserted.",
                        };

                    var nameEnd = CalleeNameEnd(invocation);
                    if (nameEnd < 0)
                        return new
                        {
                            error = $"The call to '{methodName}' at {Path.GetFileName(location.Document.FilePath)}:" +
                                    $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} already spells type " +
                                    "arguments; the VB path cannot merge the new list.",
                        };

                    AddEdit(edits, location.Document, new TextSpan(nameEnd, 0), $"(Of {argumentText})");
                    callSites++;
                }
        }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(), dryRun,
            cancellationToken, "the generification");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "generify",
            method = symbol.ToDisplayString(),
            typeParameters = abstracted.Select(entry => entry.Name).ToList(),
            explicitTypeArguments = explicitArguments.Select(entry => entry.Usage).ToList(),
            callSites,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── type mode ────────────────────────────────────────────────────────────────
    private static async Task<object> GenerifyTypeAsync(IRoslynWorkspace workspace, Solution solution,
        Document document, SyntaxNode root, SemanticModel model, string typeName, string? containingType,
        IReadOnlyList<(string Usage, ITypeSymbol Type, string Name)> usages,
        Dictionary<string, string>? constraints, bool dryRun, CancellationToken cancellationToken)
    {
        var blocks = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .Where(block => string.Equals(block.BlockStatement.Identifier.ValueText, typeName, StringComparison.Ordinal))
            .Where(block => containingType is null ||
                            string.Equals(block.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault()?
                                .BlockStatement.Identifier.ValueText, containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (blocks.Count == 0)
            return new { error = $"No class, structure, interface or module named '{typeName}' in {Path.GetFileName(document.FilePath)}." };
        if (blocks.Count > 1)
            return new { error = $"'{typeName}' has several declarations in the file; generify one at a time." };

        var block = blocks[0];
        var statement = block.BlockStatement;
        if (model.GetDeclaredSymbol(statement, cancellationToken) is not INamedTypeSymbol symbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var existing = statement.TypeParameterList?.Parameters
            .Select(parameter => parameter.Identifier.ValueText).ToList() ?? [];
        if (existing.Count > 0)
            return new
            {
                error = $"'{typeName}' already has type parameter(s) {string.Join(", ", existing)}; the VB path " +
                        "adds the first one. Make the type non-generic first, or edit the declaration by hand.",
            };

        var edits = new Dictionary<DocumentId, List<TextEdit>>();
        var abstracted = new List<(string Usage, string Name)>();

        foreach (var (usage, type, name) in usages)
        {
            var candidates = AbstractCandidates(block, model, type, cancellationToken);
            if (candidates.Count == 0)
                return new
                {
                    error = $"No abstractable occurrence of '{usage}' in '{typeName}': the type appears only where a " +
                            "type parameter cannot (an attribute, a NameOf argument, a Const declaration or a static " +
                            "member access).",
                };

            foreach (var candidate in candidates)
                AddEdit(edits, document, candidate.Span, name);

            abstracted.Add((usage, name));
        }

        AddEdit(edits, document, new TextSpan(statement.Identifier.Span.End, 0),
            TypeParameterListText(abstracted.Select(entry => (entry.Name, entry.Usage)).ToList(), constraints));

        var argumentText = string.Join(", ", abstracted.Select(entry => entry.Usage));
        var references = 0;

        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken))
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (block.Span.Contains(location.Location.SourceSpan))
                    return new
                    {
                        error = $"'{typeName}' refers to itself at {Path.GetFileName(location.Document.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1}; a self-referential type " +
                                "needs the type argument, not the concrete type, in that position — generify it by hand.",
                    };

                var targetRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                if (targetRoot is null) continue;

                var node = targetRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                var name = node.AncestorsAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault();
                if (name is null) continue;

                if (name is GenericNameSyntax { TypeArgumentList.Arguments.Count: > 0 })
                    return new
                    {
                        error = $"A reference to '{typeName}' at {Path.GetFileName(location.Document.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} already spells type " +
                                "arguments; the VB path cannot merge the new list.",
                    };

                AddEdit(edits, location.Document, name.Span,
                    $"{name.Identifier.ValueText}(Of {argumentText})");
                references++;
            }

        var applied = await ApplyAsync(workspace, solution, edits.SelectMany(pair => pair.Value).ToList(), dryRun,
            cancellationToken, "the generification");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "generify",
            type = symbol.ToDisplayString(),
            typeParameters = abstracted.Select(entry => entry.Name).ToList(),
            references = references,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The type occurrences a type parameter may replace: the type name in an <c>As</c> clause, a generic
    /// argument, an object creation's type or a local's annotation. An attribute, a NameOf argument, a Const
    /// declaration and the receiver of a static member access (`Integer.Parse`) keep the concrete type.
    /// </summary>
    private static List<TypeSyntax> AbstractCandidates(SyntaxNode container, SemanticModel model, ITypeSymbol target,
        CancellationToken cancellationToken)
    {
        var candidates = new List<TypeSyntax>();
        foreach (var node in container.DescendantNodes().OfType<TypeSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(node, cancellationToken).Type, target)) continue;
            if (!InTypePosition(node)) continue;
            if (node.Ancestors().Any(ancestor => ancestor is AttributeSyntax or NameOfExpressionSyntax)) continue;
            if (node.Ancestors().Any(ancestor => ancestor is FieldDeclarationSyntax field &&
                                                 field.Modifiers.Any(token => token.IsKind(SyntaxKind.ConstKeyword))))
                continue;
            if (node.Parent is MemberAccessExpressionSyntax access && access.Expression == node) continue;

            candidates.Add(node);
        }

        return candidates.Distinct().ToList();
    }

    /// <summary>
    /// A VB name is also a <see cref="TypeSyntax"/>, so an occurrence is abstractable only when it stands
    /// where a type is written: an <c>As</c> clause, an object creation, a generic argument or a
    /// <c>GetType(…)</c> argument — not an identifier in an expression.
    /// </summary>
    private static bool InTypePosition(TypeSyntax node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            if (current is SimpleAsClauseSyntax or ObjectCreationExpressionSyntax or TypeArgumentListSyntax
                or ArrayTypeSyntax or NullableTypeSyntax or GetTypeExpressionSyntax)
                return true;

            if (current is not (TypeSyntax or QualifiedNameSyntax or GenericNameSyntax or SimpleNameSyntax))
                return false;
        }

        return false;
    }

    /// <summary><c>(Of T)</c> and VB's <c>As { … }</c> constraints, translated from the C# spellings.</summary>
    private static string TypeParameterListText(IReadOnlyList<(string Name, string Usage)> parameters,
        Dictionary<string, string>? constraints)
    {
        var parts = new List<string>();
        foreach (var (name, usage) in parameters)
        {
            var constraint = constraints is not null &&
                             (constraints.TryGetValue(name, out var named) ||
                              constraints.TryGetValue(usage, out named))
                ? named
                : null;

            if (string.IsNullOrWhiteSpace(constraint))
            {
                parts.Add(name);
                continue;
            }

            var normalized = string.Join(", ", constraint!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Select(item => item.ToLowerInvariant() switch
                {
                    "class" => "Class",
                    "struct" => "Structure",
                    "new()" => "New",
                    _ => item,
                }));

            var values = normalized.Contains(',') ? $"{{{normalized}}}" : normalized;
            parts.Add($"{name} As {values}");
        }

        return $"(Of {string.Join(", ", parts)})";
    }

    private static int CalleeNameEnd(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access when access.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: > 0 } => -1,
        MemberAccessExpressionSyntax access => access.Name.Span.End,
        GenericNameSyntax { TypeArgumentList.Arguments.Count: > 0 } => -1,
        SimpleNameSyntax simple => simple.Span.End,
        _ => -1,
    };
}
