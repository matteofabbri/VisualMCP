using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The inline family beyond <c>inline_field</c>: a local or a single-expression member, a parameter whose
/// value is the same literal at every call site, and a helper class that only exists as one field of its
/// owner. Each one is accepted only when the substitution provably means the same thing where it lands;
/// everything else is refused with the reason instead of guessed.
/// </summary>
internal static partial class VbRefactoringTail
{
    // ── inline_symbol ────────────────────────────────────────────────────────

    /// <summary>
    /// Rider's "Inline variable/method/property" for a Visual Basic symbol. Returns null when nothing VB
    /// matches, so the C# service can continue with its own resolution.
    /// </summary>
    public static async Task<object?> InlineSymbolAsync(IRoslynWorkspace workspace, string symbolName,
        string? containingType, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;

        var solution = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return null;

        var found = await SymbolFinder.FindSourceDeclarationsAsync(solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase), SymbolFilter.Member,
            cancellationToken: cancellationToken);

        var member = found
            .Where(symbol => symbol is IMethodSymbol or IPropertySymbol)
            .Where(symbol => symbol.DeclaringSyntaxReferences.Any(reference =>
                reference.SyntaxTree.Options.Language == LanguageNames.VisualBasic))
            .FirstOrDefault(symbol => containingType is null ||
                                      string.Equals(symbol.ContainingType?.Name, containingType,
                                          StringComparison.OrdinalIgnoreCase));

        if (member is not null) return await InlineMemberAsync(workspace, solution, member, dryRun, cancellationToken);

        return await InlineLocalAsync(workspace, solution, symbolName, dryRun, cancellationToken);
    }

    private static async Task<object?> InlineMemberAsync(IRoslynWorkspace workspace, Solution solution,
        ISymbol member, bool dryRun, CancellationToken cancellationToken)
    {
        var reference = member.DeclaringSyntaxReferences.FirstOrDefault();
        var document = reference?.SyntaxTree.FilePath is null
            ? null
            : VbRefactoringSupport.DocumentAt(solution, reference.SyntaxTree.FilePath);
        if (document is null) return new { error = "Could not locate the document that declares the symbol." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        // A VB declaration reference points at the header statement: the block is what carries the body.
        var syntax = await reference!.GetSyntaxAsync(cancellationToken);
        var declaration = syntax.Parent is MethodBlockBaseSyntax or PropertyBlockSyntax ? syntax.Parent : syntax;

        var (expression, parameterNames) = member switch
        {
            IMethodSymbol => MethodExpression(declaration),
            IPropertySymbol => PropertyExpression(declaration),
            _ => ((SyntaxNode?)null, (IReadOnlyList<string>)[]),
        };

        if (expression is null)
            return new
            {
                error = "A Visual Basic member is inlined only when its body is a single Return statement " +
                        "(or its Get block is); a multi-statement body is not supported.",
            };

        var expressionText = expression.ToString().Trim();

        var references = await SymbolFinder.FindReferencesAsync(member, solution, cancellationToken);
        var edits = new List<(Document Document, TextSpan Span, string Replacement)>();
        var preview = new List<object>();

        foreach (var location in references.SelectMany(reference0 => reference0.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase) &&
                declaration.Span.Contains(location.Location.SourceSpan))
                continue;

            var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
            if (referenceRoot is null) continue;

            var node = referenceRoot.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Expression.Span.Contains(node.Span));

            var replacement = expressionText;
            var span = node.Span;

            if (invocation is not null)
            {
                span = invocation.Span;
                var arguments = invocation.ArgumentList.Arguments;
                if (arguments.Count != parameterNames.Count)
                    return new
                    {
                        error = $"A call to '{member.Name}' passes {arguments.Count} argument(s) for " +
                                $"{parameterNames.Count} parameter(s); inlining a partial call is not supported.",
                    };

                for (var index = 0; index < parameterNames.Count; index++)
                {
                    var argument = ExpressionOf(arguments[index])?.ToString().Trim();
                    if (string.IsNullOrWhiteSpace(argument)) continue;

                    replacement = Regex.Replace(replacement, $@"\b{Regex.Escape(parameterNames[index])}\b",
                        _ => argument!, RegexOptions.IgnoreCase);
                }
            }

            edits.Add((location.Document, span, $"({replacement})"));
            preview.Add(new
            {
                file = location.Document.FilePath,
                line = location.Location.GetLineSpan().StartLinePosition.Line + 1,
                replacement = $"({replacement})",
            });
        }

        if (edits.Count == 0)
            return new { error = $"'{member.Name}' has no reference outside its declaration: nothing to inline into." };

        var text = await document.GetTextAsync(cancellationToken);
        edits.Add((document, WholeLines(text, declaration.Span), string.Empty));

        var applied = await ApplyAsync(workspace, solution,
            edits.Select(edit => new TextEdit(edit.Document, edit.Span, edit.Replacement)).ToList(),
            dryRun, cancellationToken, $"inlining '{member.Name}'");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "inline_symbol",
            symbol = member.ToDisplayString(),
            kind = member.Kind.ToString(),
            referencesInlined = preview.Count,
            filesChanged = outcome.Files.Count,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            dryRun,
            applied = !dryRun,
            preview,
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static (SyntaxNode? Expression, IReadOnlyList<string> Parameters) MethodExpression(SyntaxNode declaration)
    {
        if (declaration is not MethodBlockSyntax method) return (null, []);
        if (method.SubOrFunctionStatement.IsKind(SyntaxKind.SubStatement)) return (null, []);

        if (method.Statements.Count != 1 || method.Statements[0] is not ReturnStatementSyntax { Expression: { } returned })
            return (null, []);

        return (returned, method.SubOrFunctionStatement.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.Identifier.ValueText)
            .ToList());
    }

    private static (SyntaxNode? Expression, IReadOnlyList<string> Parameters) PropertyExpression(SyntaxNode declaration)
    {
        if (declaration is not PropertyBlockSyntax property) return (null, []);

        var getter = property.Accessors.FirstOrDefault(accessor =>
            accessor.AccessorStatement.IsKind(SyntaxKind.GetAccessorStatement));
        if (getter is null || getter.Statements.Count != 1 ||
            getter.Statements[0] is not ReturnStatementSyntax { Expression: { } returned })
            return (null, []);

        return (returned, []);
    }

    private static async Task<object?> InlineLocalAsync(IRoslynWorkspace workspace, Solution solution,
        string symbolName, bool dryRun, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Language != LanguageNames.VisualBasic) continue;

            foreach (var document in project.Documents)
            {
                if (document.SourceCodeKind != SourceCodeKind.Regular) continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                var declarator = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(candidate => candidate.Names.Any(name =>
                        string.Equals(name.Identifier.ValueText, symbolName, StringComparison.Ordinal)));
                if (declarator is null) continue;

                var statement = declarator.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
                if (statement is null) continue;

                if (statement.Declarators.Count != 1 || declarator.Names.Count != 1)
                    return new { error = $"'{symbolName}' shares its declaration with other variables; split it before inlining." };

                var initializer = declarator.Initializer?.Value;
                if (initializer is null)
                    return new { error = $"The local '{symbolName}' has no initializer: inlining it would change what each use evaluates." };

                var symbol = model.GetDeclaredSymbol(declarator.Names[0], cancellationToken) as ILocalSymbol;
                if (symbol is null) return new { error = $"'{symbolName}' did not resolve to a local variable." };

                var method = statement.Ancestors().OfType<MethodBlockBaseSyntax>().FirstOrDefault();
                if (method is null) return new { error = $"'{symbolName}' is not declared inside a method body." };

                var uses = method.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(identifier => SymbolEqualityComparer.Default.Equals(
                        model.GetSymbolInfo(identifier, cancellationToken).Symbol, symbol))
                    .ToList();

                if (uses.Count == 0)
                    return new { error = $"The local '{symbolName}' is declared but never read: nothing to inline." };

                if (uses.Any(IsWritten))
                    return new { error = $"The local '{symbolName}' is written outside its declaration; inlining would lose the assignment." };

                if (uses.Any(identifier => PassedByRef(identifier, model, cancellationToken)))
                    return new { error = $"The local '{symbolName}' is passed ByRef, so the callee can write it; inlining would lose that." };

                var replacement = "(" + initializer.ToString().Trim() + ")";
                var text = await document.GetTextAsync(cancellationToken);
                var edits = uses.Select(identifier => new TextEdit(document, identifier.Span, replacement)).ToList();
                edits.Add(new TextEdit(document, WholeLines(text, statement.Span), string.Empty));

                var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken,
                    $"inlining '{symbolName}'");
                if (applied.Error is not null) return applied.Error;
                var outcome = applied.Value!;

                return new
                {
                    @refactoring = "inline_symbol",
                    symbol = symbol.ToDisplayString(),
                    kind = "local variable",
                    referencesInlined = uses.Count,
                    initializerExpression = replacement,
                    file = document.FilePath,
                    compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
                    dryRun,
                    applied = !dryRun,
                    files = outcome.Files.Select(entry => new
                    {
                        file = entry.Document.FilePath,
                        preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
                    }).ToList(),
                    note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
                };
            }
        }

        return new { error = $"'{symbolName}' is not a Visual Basic method, property or local variable." };
    }

    // ── inline_parameter ─────────────────────────────────────────────────────

    public static async Task<object> InlineParameterAsync(IRoslynWorkspace workspace, string filePath,
        string methodName, string parameterName, string? containingType, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var candidates = root.DescendantNodes().OfType<MethodBlockSyntax>()
            .Where(method => string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, methodName,
                StringComparison.Ordinal))
            .Where(method => containingType is null || string.Equals(
                model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken)?.ContainingType?.Name,
                containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return new { error = $"No method '{methodName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1) return new { error = $"Several methods named '{methodName}'; pass containingType." };

        var declaration = candidates[0];
        var header = declaration.SubOrFunctionStatement;
        if (model.GetDeclaredSymbol(header, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var refusal = RefuseInlineParameter(header, method);
        if (refusal is not null) return new { error = refusal };

        var parameters = header.ParameterList.Parameters;
        var index = -1;
        for (var position = 0; position < parameters.Count; position++)
        {
            if (string.Equals(parameters[position].Identifier.Identifier.ValueText, parameterName, StringComparison.Ordinal))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
            return new
            {
                error = $"'{methodName}' has no parameter '{parameterName}'.",
                parameters = parameters.Select(parameter => parameter.Identifier.Identifier.ValueText).ToList(),
            };

        var parameterSymbol = method.Parameters[index];
        if (parameterSymbol.RefKind != RefKind.None)
            return new { error = $"'{parameterName}' is passed ByRef; moving the value into the body would lose the write back." };
        if (parameterSymbol.IsParams)
            return new { error = $"'{parameterName}' is a ParamArray; it cannot be inlined." };

        foreach (var call in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            if (SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(call, cancellationToken).Symbol, method))
                return new { error = $"'{methodName}' calls itself; the recursive call would keep the argument that is being removed." };

        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        var callSites = new List<(Document Document, InvocationExpressionSyntax Invocation, ExpressionSyntax Argument)>();
        var unsupported = new List<string>();

        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase) &&
                declaration.Span.Contains(location.Location.SourceSpan))
                continue;

            var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
            var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is null) continue;

            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Expression.Span.Contains(node.Span));
            if (invocation is null)
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{location.Location.GetLineSpan().StartLinePosition.Line + 1} (method group or AddressOf)");
                continue;
            }

            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count <= index)
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (omits the argument)");
                continue;
            }

            if (arguments[index] is not SimpleArgumentSyntax simple)
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (omitted or range argument)");
                continue;
            }

            if (simple.NameColonEquals is not null)
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (named argument)");
                continue;
            }

            if (simple.Expression is not { } argument)
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (omitted argument)");
                continue;
            }

            callSites.Add((location.Document, invocation, argument));
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) cannot be inlined (the argument is not where it would have to be); nothing was written.",
                uses = unsupported.Take(10).ToList(),
            };

        if (callSites.Count == 0) return new { error = $"No call sites of '{methodName}' were found; nothing to inline into." };

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callDocument, invocation, argument) in callSites)
        {
            var argumentModel = await callDocument.GetSemanticModelAsync(cancellationToken);
            if (argumentModel is null) return new { error = $"Could not read the semantic model of {callDocument.FilePath}." };

            var dependence = CallSiteDependence(argument, argumentModel, cancellationToken);
            if (dependence is not null)
                return new
                {
                    error = $"The argument at {Path.GetFileName(callDocument.FilePath)}:" +
                            $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {dependence}; " +
                            "inlining it would move the evaluation into the method, which changes what it means.",
                };

            values.Add(Normalize(argument));
        }

        if (values.Count > 1)
            return new
            {
                error = "The call sites pass different values, so removing the parameter would change their behaviour.",
                values = values.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            };

        var substitution = values.Single();

        var edits = new List<TextEdit>();
        foreach (var group in callSites.GroupBy(site => site.Document.Id))
        {
            var target = group.First().Document;
            foreach (var (_, invocation, _) in group)
                edits.Add(new TextEdit(target, ArgumentRemoval(invocation, index), string.Empty));
        }

        var text = await document.GetTextAsync(cancellationToken);
        foreach (var identifier in declaration.DescendantNodes().OfType<IdentifierNameSyntax>()
                     .Where(identifier => SymbolEqualityComparer.Default.Equals(
                         model.GetSymbolInfo(identifier, cancellationToken).Symbol, parameterSymbol)))
            edits.Add(new TextEdit(document, identifier.Span, $"({substitution})"));

        edits.Add(new TextEdit(document, ParameterRemoval(parameters, index), string.Empty));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken,
            $"inlining '{parameterName}'");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "inline_parameter",
            method = method.ToDisplayString(),
            parameter = parameterName,
            inlinedValue = substitution,
            callSites = callSites.Count,
            filesChanged = outcome.Files.Count,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            dryRun,
            applied = !dryRun,
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static string? RefuseInlineParameter(MethodStatementSyntax header, IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary) return $"'{method.Name}' is a {method.MethodKind}, not an ordinary method.";
        if (method.IsStatic) return $"'{method.Name}' is Shared; a shared method's call sites are not reached by an instance receiver.";
        if (method.IsAbstract || method.IsVirtual || method.IsOverride)
            return $"'{method.Name}' is MustOverride/Overridable/Overrides: overrides and implementations would have to change too.";
        if (method.ContainingType is { TypeKind: TypeKind.Interface }) return "An interface method cannot lose a parameter this way.";
        if (header.ParameterList.Parameters.Count == 0) return $"'{method.Name}' has no parameters.";
        return null;
    }

    /// <summary>Null when the argument can be evaluated anywhere; otherwise why it cannot.</summary>
    private static string? CallSiteDependence(ExpressionSyntax argument, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var node in argument.DescendantNodesAndSelf())
        {
            if (node is MeExpressionSyntax or MyBaseExpressionSyntax)
                return "reads 'Me'";

            if (node is not IdentifierNameSyntax identifier) continue;

            switch (model.GetSymbolInfo(identifier, cancellationToken).Symbol)
            {
                case ILocalSymbol:
                case IParameterSymbol:
                    return $"reads the local/parameter '{identifier.Identifier.ValueText}'";
                case IFieldSymbol { IsStatic: false }:
                case IPropertySymbol { IsStatic: false }:
                case IEventSymbol { IsStatic: false }:
                    return $"reads the instance member '{identifier.Identifier.ValueText}'";
            }
        }

        return null;
    }

    private static string Normalize(SyntaxNode node) =>
        Regex.Replace(node.ToString().Trim(), @"\s+", " ");

    // ── inline_class ─────────────────────────────────────────────────────────

    public static async Task<object> InlineClassAsync(IRoslynWorkspace workspace, string filePath,
        string ownerTypeName, string fieldName, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var owner = FindTypeBlock(root, ownerTypeName);
        if (owner is null) return new { error = $"No type '{ownerTypeName}' in {Path.GetFileName(filePath)}." };

        var field = owner.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Declarators.Count == 1 &&
                                         candidate.Declarators[0].Names.Count == 1 &&
                                         string.Equals(candidate.Declarators[0].Names[0].Identifier.ValueText,
                                             fieldName, StringComparison.Ordinal));
        if (field is null) return new { error = $"No single-variable field '{fieldName}' in '{ownerTypeName}'." };
        if (HasModifier(field.Modifiers, SyntaxKind.SharedKeyword))
            return new { error = $"'{fieldName}' is Shared; only an instance field can be inlined into its owner." };

        var declarator = field.Declarators[0];
        var creation = declarator.AsClause switch
        {
            AsNewClauseSyntax fresh => fresh.NewExpression as ObjectCreationExpressionSyntax,
            _ => declarator.Initializer?.Value as ObjectCreationExpressionSyntax,
        };

        if (creation is null ||
            (creation.ArgumentList is not null && creation.ArgumentList.Arguments.Count > 0) ||
            creation.Initializer is not null)
            return new { error = $"'{fieldName}' must be initialised with 'As New Helper()' (no arguments, no object initializer)." };

        if (model.GetTypeInfo(creation, cancellationToken).Type is not INamedTypeSymbol helper)
            return new { error = $"The type of '{fieldName}' did not resolve." };

        var refusal = RefuseHelper(helper);
        if (refusal is not null) return new { error = refusal };

        var helperDeclaration = root.DescendantNodes().OfType<ClassBlockSyntax>()
            .FirstOrDefault(block => string.Equals(block.BlockStatement.Identifier.ValueText, helper.Name,
                StringComparison.Ordinal));
        if (helperDeclaration is null)
            return new { error = $"'{helper.ToDisplayString()}' is not declared as a class in {Path.GetFileName(filePath)}." };

        var helperReferences = await SymbolFinder.FindReferencesAsync(helper, solution, cancellationToken);
        foreach (var location in helperReferences.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (field.Span.Contains(location.Location.SourceSpan)) continue;

            return new
            {
                error = $"'{helper.ToDisplayString()}' is also used at " +
                        $"{Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}; " +
                        "inlining it would leave that use dangling.",
            };
        }

        var fieldSymbol = model.GetDeclaredSymbol(declarator.Names[0], cancellationToken) as IFieldSymbol;
        if (fieldSymbol is null) return new { error = $"'{fieldName}' did not resolve to a field." };

        var accesses = new List<MemberAccessExpressionSyntax>();
        var fieldReferences = await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken);
        foreach (var location in fieldReferences.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (declarator.Names[0].Span.Contains(location.Location.SourceSpan)) continue;

            var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
            var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is null) continue;

            var access = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Expression.Span.Contains(node.Span));
            if (access is null)
                return new
                {
                    error = $"'{fieldName}' is used as a value at " +
                            $"{Path.GetFileName(location.Document.FilePath)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}; " +
                            "only a 'field.Member' access can be inlined.",
                };

            if (!string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))
                return new { error = $"'{fieldName}' is accessed from another file; inlining only inside '{ownerTypeName}' is supported." };

            accesses.Add(access);
        }

        var ownerSymbol = model.GetDeclaredSymbol(owner.BlockStatement, cancellationToken) as INamedTypeSymbol;
        var mover = helperDeclaration.Members
            .Where(member => member is not ConstructorBlockSyntax)
            .ToList();

        foreach (var member in mover)
        {
            var name = MemberName(member);
            if (name is not null && ownerSymbol?.GetMembers(name).Length > 0)
                return new { error = $"'{ownerTypeName}' already has a member named '{name}'; inlining would collide." };
        }

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var memberIndentation = IndentationOf(text, owner.BlockStatement.SpanStart) + "    ";

        var insertion = string.Join(newLine,
            mover.Select(member => Reindent(member.ToString().TrimEnd(),
                IndentationOf(text, member.SpanStart), memberIndentation, newLine)));

        var edits = new List<TextEdit>
        {
            new(document, WholeLines(text, field.Span), string.Empty),
            new(document, WholeLines(text, helperDeclaration.Span), string.Empty),
            new(document, new TextSpan(BeforeEndOfType(text, owner), 0), insertion + newLine),
        };

        foreach (var access in accesses)
            edits.Add(new TextEdit(document, access.Span, access.Name.ToString()));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "inlining the class");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "inline_class",
            owner = ownerSymbol?.ToDisplayString() ?? ownerTypeName,
            inlined = helper.ToDisplayString(),
            field = fieldName,
            members = mover.Select(MemberName).Where(name => name is not null).ToList(),
            file = document.FilePath,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            dryRun,
            applied = !dryRun,
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private static string? RefuseHelper(INamedTypeSymbol helper)
    {
        if (helper.TypeKind != TypeKind.Class) return $"'{helper.ToDisplayString()}' is not a class.";
        if (helper.IsStatic) return $"'{helper.ToDisplayString()}' is a Module; it has no instance state to inline.";
        if (helper.ContainingType is not null) return $"'{helper.ToDisplayString()}' is nested; inlining a nested helper is not supported.";
        if (helper.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            return $"'{helper.ToDisplayString()}' derives from '{baseType.ToDisplayString()}'; the owner would have to inherit that too.";
        if (helper.AllInterfaces.Length > 0)
            return $"'{helper.ToDisplayString()}' implements interfaces; the owner would have to implement them too.";
        if (helper.GetTypeMembers().Length > 0)
            return $"'{helper.ToDisplayString()}' declares nested types; those are not inlined.";
        if (helper.GetMembers().Any(member => member.IsStatic && member is not IMethodSymbol { MethodKind: MethodKind.Constructor }))
            return $"'{helper.ToDisplayString()}' has Shared members; only instance members are inlined.";

        foreach (var constructor in helper.InstanceConstructors.Where(constructor => !constructor.IsImplicitlyDeclared))
        {
            if (constructor.Parameters.Length > 0)
                return $"'{helper.ToDisplayString()}' has a constructor with parameters; it cannot be replaced by a field initializer.";

            var declaration = constructor.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<ConstructorBlockSyntax>()
                .FirstOrDefault();

            if (declaration is { Statements.Count: > 0 })
                return $"'{helper.ToDisplayString()}' has a constructor with a body; it cannot be replaced by a field initializer.";
        }

        return null;
    }
}
