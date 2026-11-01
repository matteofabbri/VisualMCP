using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The move family beyond <c>move_type</c>/<c>move_to_folder</c>/<c>move_to_namespace</c>: a Shared
/// member to another type, an instance method that becomes a Shared method taking its receiver, and a
/// type that changes scope. Each one rewrites the references it can find through the symbol, re-points
/// the declaration, and lets the compiler decide whether the result holds.
/// </summary>
internal static partial class VbRefactoringTail
{
    private static async Task<INamedTypeSymbol?> FindTypeSymbolAsync(Solution solution, string name,
        CancellationToken cancellationToken)
    {
        var found = await SymbolFinder.FindSourceDeclarationsAsync(solution,
            candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase), SymbolFilter.Type,
            cancellationToken: cancellationToken);

        var types = found.OfType<INamedTypeSymbol>().ToList();
        if (name.Contains('.')) types = types.Where(type => type.ToDisplayString() == name).ToList();

        return types.Count == 1 ? types[0] : null;
    }

    private static ISymbol? SymbolOfMember(StatementSyntax member, SemanticModel model, CancellationToken cancellationToken) =>
        member switch
        {
            MethodBlockSyntax method => model.GetDeclaredSymbol(method.SubOrFunctionStatement, cancellationToken),
            MethodStatementSyntax statement => model.GetDeclaredSymbol(statement, cancellationToken),
            PropertyBlockSyntax property => model.GetDeclaredSymbol(property.PropertyStatement, cancellationToken),
            PropertyStatementSyntax statement => model.GetDeclaredSymbol(statement, cancellationToken),
            FieldDeclarationSyntax field when field.Declarators.Count == 1 && field.Declarators[0].Names.Count == 1
                => model.GetDeclaredSymbol(field.Declarators[0].Names[0], cancellationToken),
            _ => null,
        };

    /// <summary>The span a qualified rewrite has to cover: the whole <c>x.Name</c>, not just the name.</summary>
    private static TextSpan ReferenceSpan(SyntaxNode node) => node.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name.Span == node.Span => access.Span,
        QualifiedNameSyntax qualified when qualified.Right.Span == node.Span => qualified.Span,
        _ => node.Span,
    };

    private static string NameOfReference(SyntaxNode node) =>
        node is SimpleNameSyntax simple ? simple.Identifier.ValueText : node.ToString();

    // ── move_static_members ──────────────────────────────────────────────────

    public static async Task<object> MoveStaticMembersAsync(IRoslynWorkspace workspace, string filePath,
        string sourceTypeName, string[] memberNames, string targetTypeName, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (memberNames is null || memberNames.Length == 0) return new { error = "Name the members to move." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var source = FindTypeBlock(root, sourceTypeName);
        if (source is null) return new { error = $"No type '{sourceTypeName}' in {Path.GetFileName(filePath)}." };
        if (model.GetDeclaredSymbol(source.BlockStatement, cancellationToken) is not INamedTypeSymbol sourceSymbol)
            return new { error = $"'{sourceTypeName}' did not resolve to a type." };
        if (sourceSymbol.Arity > 0)
            return new { error = $"'{sourceTypeName}' is generic; moving members out of a generic type is not supported." };

        var targetSymbol = await FindTypeSymbolAsync(solution, targetTypeName, cancellationToken);
        if (targetSymbol is null) return new { error = $"No single type named '{targetTypeName}' in the solution." };
        if (SymbolEqualityComparer.Default.Equals(targetSymbol, sourceSymbol))
            return new { error = $"'{sourceTypeName}' is already the target." };

        var targetReference = targetSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        var targetDocument = targetReference?.SyntaxTree.FilePath is null
            ? null
            : VbRefactoringSupport.DocumentAt(solution, targetReference.SyntaxTree.FilePath);
        if (targetDocument is null)
            return new { error = $"'{targetSymbol.ToDisplayString()}' is not declared in a Visual Basic file of the solution." };

        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (targetRoot is null) return new { error = "Could not read the target document." };
        var targetType = FindTypeBlock(targetRoot, targetSymbol.Name);
        if (targetType is null) return new { error = $"Could not find the declaration of '{targetSymbol.Name}'." };

        var selected = new List<StatementSyntax>();
        var symbols = new List<ISymbol>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var member = source.Members.FirstOrDefault(candidate => MemberName(candidate) == name);
            if (member is null)
                return new
                {
                    error = $"No movable member '{name}' in '{sourceTypeName}' (methods, properties and single-variable fields only).",
                    available = source.Members.Select(MemberName).Where(candidate => candidate is not null).Distinct().ToList(),
                };

            if (!HasModifier(ModifiersOf(member), SyntaxKind.SharedKeyword))
                return new { error = $"'{name}' is an instance member; use move_instance_method." };

            if (SymbolOfMember(member, model, cancellationToken) is not { } symbol)
                return new { error = $"'{name}' did not resolve." };

            if (targetSymbol.GetMembers(name).Length > 0)
                return new { error = $"'{targetSymbol.ToDisplayString()}' already has a member named '{name}'." };

            selected.Add(member);
            symbols.Add(symbol);
        }

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var edits = new List<TextEdit>();

        foreach (var symbol in symbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            foreach (var location in references.SelectMany(reference => reference.Locations)
                         .Where(location => location.Location.IsInSource))
            {
                if (selected.Any(member => member.Span.Contains(location.Location.SourceSpan))) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is not (IdentifierNameSyntax or GenericNameSyntax)) continue;

                edits.Add(new TextEdit(location.Document, ReferenceSpan(node),
                    $"{targetSymbol.Name}.{NameOfReference(node)}"));
            }
        }

        foreach (var member in selected)
            edits.Add(new TextEdit(document, WholeLines(text, member.Span), string.Empty));

        var targetText = targetDocument.Id == document.Id ? text : await targetDocument.GetTextAsync(cancellationToken);
        var targetIndent = IndentationOf(targetText, targetType.BlockStatement.SpanStart) + "    ";
        var moved = string.Join(newLine + newLine, selected.Select(member =>
            Reindent(member.ToString().TrimEnd(), IndentationOf(text, member.SpanStart), targetIndent, newLine)));

        edits.Add(new TextEdit(targetDocument, new TextSpan(BeforeEndOfType(targetText, targetType), 0), moved + newLine));

        var imports = await EnsureImportsAsync(solution, edits, [NamespaceOf(targetSymbol)], cancellationToken);
        if (imports.Error is not null) return imports.Error!;

        var applied = await ApplyAsync(workspace, solution, imports.Edits!, dryRun, cancellationToken, "the move");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "move_static_members",
            from = sourceSymbol.ToDisplayString(),
            to = targetSymbol.ToDisplayString(),
            members = memberNames,
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

    // ── move_instance_method ─────────────────────────────────────────────────

    public static async Task<object> MoveInstanceMethodAsync(IRoslynWorkspace workspace, string filePath,
        string typeName, string methodName, string targetTypeName, string receiverName, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!IsValidIdentifier(receiverName)) return new { error = $"'{receiverName}' is not a valid parameter name." };
        if (string.Equals(receiverName, typeName, StringComparison.OrdinalIgnoreCase))
            return new
            {
                error = $"'{receiverName}' is the source type's own name: Visual Basic would read " +
                        $"'({typeName} {receiverName})' as one identifier. Pass another receiverName.",
            };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var source = FindTypeBlock(root, typeName);
        if (source is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(filePath)}." };
        if (model.GetDeclaredSymbol(source.BlockStatement, cancellationToken) is not INamedTypeSymbol sourceSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var candidates = source.Members.OfType<MethodBlockSyntax>()
            .Where(method => string.Equals(method.SubOrFunctionStatement.Identifier.ValueText, methodName,
                StringComparison.Ordinal))
            .ToList();
        if (candidates.Count == 0) return new { error = $"No method '{methodName}' with a body in '{typeName}'." };
        if (candidates.Count > 1) return new { error = $"'{methodName}' has overloads; move them one at a time." };

        var method = candidates[0];
        var header = method.SubOrFunctionStatement;
        if (model.GetDeclaredSymbol(header, cancellationToken) is not IMethodSymbol methodSymbol)
            return new { error = $"'{methodName}' did not resolve to a method." };

        var refusal = RefuseMoveInstanceMethod(method, methodSymbol);
        if (refusal is not null) return new { error = refusal };

        var targetSymbol = await FindTypeSymbolAsync(solution, targetTypeName, cancellationToken);
        if (targetSymbol is null) return new { error = $"No single type named '{targetTypeName}' in the solution." };
        if (SymbolEqualityComparer.Default.Equals(targetSymbol, sourceSymbol))
            return new { error = $"'{typeName}' is already the source and the target." };
        if (targetSymbol.GetMembers(methodName).Length > 0)
            return new { error = $"'{targetSymbol.ToDisplayString()}' already has a member named '{methodName}'." };

        var targetReference = targetSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        var targetDocument = targetReference?.SyntaxTree.FilePath is null
            ? null
            : VbRefactoringSupport.DocumentAt(solution, targetReference.SyntaxTree.FilePath);
        if (targetDocument is null)
            return new { error = $"'{targetSymbol.ToDisplayString()}' is not declared in a Visual Basic file of the solution." };

        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (targetRoot is null) return new { error = "Could not read the target document." };
        var targetType = FindTypeBlock(targetRoot, targetSymbol.Name);
        if (targetType is null) return new { error = $"Could not find the declaration of '{targetSymbol.Name}'." };

        if (header.ParameterList is not null &&
            header.ParameterList.Parameters.Any(parameter =>
                string.Equals(parameter.Identifier.Identifier.ValueText, receiverName, StringComparison.Ordinal)))
            return new { error = $"'{methodName}' already has a parameter named '{receiverName}'; pass another receiverName." };

        // ── The body: Me and the implicit instance members go through the receiver ───────
        var bodyEdits = new List<(int Start, int Length, string Replacement)>();
        foreach (var node in method.DescendantNodes())
        {
            if (node is MeExpressionSyntax)
            {
                bodyEdits.Add((node.SpanStart, node.Span.Length, receiverName));
                continue;
            }

            if (node is not IdentifierNameSyntax identifier) continue;

            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
            if (symbol.IsStatic) continue;
            if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, sourceSymbol)) continue;
            if (SymbolEqualityComparer.Default.Equals(symbol, methodSymbol)) continue;
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name.Span == identifier.Span) continue;

            if (symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
                return new
                {
                    error = $"'{symbol.Name}' is Private/Protected; the moved method could not read it from another type.",
                };

            bodyEdits.Add((identifier.SpanStart, identifier.Span.Length,
                $"{receiverName}.{identifier.Identifier.ValueText}"));
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(sourceText);
        var rewritten = RewriteText(method.ToString(),
            bodyEdits.Select(edit => (edit.Start - method.SpanStart, edit.Length, edit.Replacement)));

        // ── The header: Shared, and the receiver as the first parameter ─────────────────
        var headerStart = header.SpanStart - method.SpanStart;
        var insertions = new List<(int At, string Text)>
        {
            (header.Modifiers.Count > 0 ? header.Modifiers[^1].Span.End - header.SpanStart : 0, " Shared"),
        };

        if (header.ParameterList is not null)
        {
            var comma = header.ParameterList.Parameters.Count > 0 ? ", " : string.Empty;
            insertions.Add((header.ParameterList.OpenParenToken.Span.End - header.SpanStart,
                $"{receiverName} As {typeName}{comma}"));
        }
        else
        {
            insertions.Add((header.Identifier.Span.End - header.SpanStart, $"({receiverName} As {typeName})"));
        }

        var newHeader = RewriteText(header.ToString(), insertions.Select(insertion => (insertion.At, 0, insertion.Text)));
        var movedText = rewritten[..headerStart] + newHeader + rewritten[(headerStart + header.Span.Length)..];

        // ── Call sites ──────────────────────────────────────────────────────────────────
        var edits = new List<TextEdit>();
        var unsupported = new List<string>();

        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase) &&
                method.Span.Contains(location.Location.SourceSpan))
                continue;

            var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
            var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is null) continue;

            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Expression.Span.Contains(node.Span));
            if (invocation is null)
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (method group or AddressOf)");
                continue;
            }

            var argumentText = string.Join(", ", invocation.ArgumentList.Arguments.Select(argument => argument.ToString()));
            string receiver;
            string callee;

            if (invocation.Expression is MemberAccessExpressionSyntax member && member.Name.Span == node.Span)
            {
                receiver = member.Expression.ToString();
                callee = member.Name.ToString();
            }
            else if (invocation.Expression is IdentifierNameSyntax or GenericNameSyntax)
            {
                receiver = "Me";
                callee = invocation.Expression.ToString();
            }
            else
            {
                unsupported.Add($"{Path.GetFileName(location.Document.FilePath)}:" +
                                $"{invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (unexpected call form)");
                continue;
            }

            var combined = argumentText.Length == 0 ? receiver : receiver + ", " + argumentText;
            edits.Add(new TextEdit(location.Document, invocation.Span, $"{targetSymbol.Name}.{callee}({combined})"));
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} use(s) of '{methodName}' are not plain calls; moving would change their meaning.",
                uses = unsupported.Take(10).ToList(),
            };

        edits.Add(new TextEdit(document, WholeLines(sourceText, method.Span), string.Empty));

        var targetText = targetDocument.Id == document.Id
            ? sourceText
            : await targetDocument.GetTextAsync(cancellationToken);
        var moved = Reindent(movedText, IndentationOf(sourceText, method.SpanStart),
            IndentationOf(targetText, targetType.BlockStatement.SpanStart) + "    ", newLine);
        edits.Add(new TextEdit(targetDocument, new TextSpan(BeforeEndOfType(targetText, targetType), 0), moved + newLine));

        var imports = await EnsureImportsAsync(solution, edits,
            new[] { NamespaceOf(targetSymbol), NamespaceOf(sourceSymbol) }
                .Where(value => value is not null).Select(value => value!).ToList(),
            cancellationToken);
        if (imports.Error is not null) return imports.Error!;

        var applied = await ApplyAsync(workspace, solution, imports.Edits!, dryRun, cancellationToken, "the move");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "move_instance_method",
            from = $"{sourceSymbol.ToDisplayString()}.{methodName}",
            to = targetSymbol.ToDisplayString(),
            receiver = receiverName,
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

    private static string? RefuseMoveInstanceMethod(MethodBlockSyntax method, IMethodSymbol symbol)
    {
        if (symbol.MethodKind != MethodKind.Ordinary) return $"'{symbol.Name}' is a {symbol.MethodKind}, not an ordinary method.";
        if (symbol.IsStatic) return $"'{symbol.Name}' is Shared; use move_static_members.";
        if (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride)
            return $"'{symbol.Name}' is MustOverride/Overridable/Overrides: its contract with the hierarchy would break.";
        if (method.DescendantNodes().OfType<MyBaseExpressionSyntax>().Any())
            return $"'{symbol.Name}' uses 'MyBase'; a Shared method in another type has no base to call.";

        return null;
    }

    // ── move_type_to_outer_scope ─────────────────────────────────────────────

    public static async Task<object> MoveTypeToOuterScopeAsync(IRoslynWorkspace workspace, string filePath,
        string typeName, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var nested = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .FirstOrDefault(block => string.Equals(block.BlockStatement.Identifier.ValueText, typeName,
                StringComparison.Ordinal) && block.Parent is TypeBlockSyntax);
        if (nested is null) return new { error = $"No nested type '{typeName}' in {Path.GetFileName(filePath)}." };

        var outer = (TypeBlockSyntax)nested.Parent!;
        if (model.GetDeclaredSymbol(nested.BlockStatement, cancellationToken) is not INamedTypeSymbol nestedSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var edits = new List<TextEdit>();
        var referencesRewritten = 0;

        var references = await SymbolFinder.FindReferencesAsync(nestedSymbol, solution, cancellationToken);
        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase) &&
                nested.Span.Contains(location.Location.SourceSpan))
                continue;

            var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
            var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is not (IdentifierNameSyntax or GenericNameSyntax)) continue;

            edits.Add(new TextEdit(location.Document, ReferenceSpan(node), typeName));
            referencesRewritten++;
        }

        // The type leaves the container and becomes its sibling, at the container's indentation.
        var outerIndent = IndentationOf(text, outer.BlockStatement.SpanStart);
        var insertion = AfterEndOfType(text, outer);
        edits.Add(new TextEdit(document, WholeLines(text, nested.Span), string.Empty));
        edits.Add(new TextEdit(document, new TextSpan(insertion, 0),
            newLine + Reindent(nested.ToString().TrimEnd(), IndentationOf(text, nested.SpanStart), outerIndent, newLine) + newLine));

        var imports = await EnsureImportsAsync(solution, edits, [NamespaceOf(nestedSymbol)], cancellationToken);
        if (imports.Error is not null) return imports.Error!;

        var applied = await ApplyAsync(workspace, solution, imports.Edits!, dryRun, cancellationToken, "the move");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "move_type_to_outer_scope",
            type = nestedSymbol.ToDisplayString(),
            movedOutOf = outer.BlockStatement.Identifier.ValueText,
            file = document.FilePath,
            referencesRewritten,
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

    // ── move_type_to_nested_scope ────────────────────────────────────────────

    public static async Task<object> MoveTypeToNestedScopeAsync(IRoslynWorkspace workspace, string filePath,
        string typeName, string targetTypeName, string? targetFilePath, bool dryRun,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var declarations = root.DescendantNodes().OfType<TypeBlockSyntax>()
            .Where(block => string.Equals(block.BlockStatement.Identifier.ValueText, typeName, StringComparison.Ordinal))
            .ToList();
        if (declarations.Count == 0) return new { error = $"No type '{typeName}' in {Path.GetFileName(filePath)}." };
        if (declarations.Count > 1) return new { error = $"Several types named '{typeName}' in {Path.GetFileName(filePath)}." };

        var source = declarations[0];
        if (source.Parent is not (CompilationUnitSyntax or NamespaceBlockSyntax))
            return new
            {
                error = $"'{typeName}' is already nested. Use move_type_to_outer_scope to pull it out of its container first.",
            };

        if (model.GetDeclaredSymbol(source.BlockStatement, cancellationToken) is not INamedTypeSymbol sourceSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        // ── The target: a type of the same project that can hold members ──────────────
        var targets = new List<(Document Document, TypeBlockSyntax Block, INamedTypeSymbol Symbol)>();
        foreach (var projectDocument in document.Project.Documents)
        {
            if (targetFilePath is not null && projectDocument.FilePath is not null &&
                !string.Equals(Path.GetFullPath(projectDocument.FilePath), Path.GetFullPath(targetFilePath),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var targetRoot = await projectDocument.GetSyntaxRootAsync(cancellationToken);
            var targetModel = await projectDocument.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            foreach (var block in targetRoot.DescendantNodes().OfType<TypeBlockSyntax>())
            {
                if (!string.Equals(block.BlockStatement.Identifier.ValueText, targetTypeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (block is EnumBlockSyntax) continue;
                if (block is not (ClassBlockSyntax or StructureBlockSyntax or ModuleBlockSyntax or InterfaceBlockSyntax)) continue;
                if (projectDocument.Id == document.Id &&
                    (block.Span.Contains(source.Span) || source.Span.Contains(block.Span))) continue;
                if (targetModel.GetDeclaredSymbol(block.BlockStatement, cancellationToken) is not INamedTypeSymbol symbol) continue;

                targets.Add((projectDocument, block, symbol));
            }
        }

        if (targets.Count == 0)
            return new { error = $"No type '{targetTypeName}' that can hold '{typeName}' in project '{document.Project.Name}'." };
        if (targets.Count > 1)
            return new
            {
                error = $"Several types named '{targetTypeName}'; pass targetFilePath.",
                candidates = targets.Select(target => target.Document.FilePath).ToList(),
            };

        var (targetDocument, targetBlock, targetSymbol) = targets[0];
        if (targetDocument.Project.Id != document.Project.Id)
            return new { error = "The target type is in another project: moving the declaration would change its assembly." };

        // ── Edits, all in the coordinates of the text they were read from ────────────
        var edits = new Dictionary<DocumentId, List<(TextSpan Span, string Text)>>();
        var documents = new Dictionary<DocumentId, Document>();
        var referenceCount = 0;

        foreach (var projectDocument in solution.Projects.SelectMany(project => project.Documents))
        {
            var isSource = projectDocument.Id == document.Id;
            var modelRoot = isSource ? root : await projectDocument.GetSyntaxRootAsync(cancellationToken);
            var referenceModel = isSource ? model : await projectDocument.GetSemanticModelAsync(cancellationToken);
            if (modelRoot is null || referenceModel is null) continue;

            var list = new List<(TextSpan Span, string Text)>();
            foreach (var node in modelRoot.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(
                        referenceModel.GetSymbolInfo(node, cancellationToken).Symbol, sourceSymbol))
                    continue;
                if (source.Span.Contains(node.Span)) continue;
                if (projectDocument.Id == targetDocument.Id && targetBlock.Span.Contains(node.Span)) continue;

                var span = ReferenceSpan(node);
                if (list.Any(existing => existing.Span.Contains(span))) continue;

                list.Add((span, $"{SpellingOf(targetSymbol, modelRoot)}.{typeName}"));
                referenceCount++;
            }

            if (list.Count == 0) continue;

            edits[projectDocument.Id] = list;
            documents[projectDocument.Id] = projectDocument;
        }

        if (!edits.TryGetValue(document.Id, out var sourceEdits))
        {
            sourceEdits = [];
            edits[document.Id] = sourceEdits;
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        sourceEdits.Add((WholeLines(sourceText, source.Span), string.Empty));
        documents[document.Id] = document;

        var sourceWithout = RewriteText(sourceText.ToString(),
            sourceEdits.Select(edit => (edit.Span.Start, edit.Span.Length, edit.Text)));

        var deleteSource = HasNoDeclarations(sourceWithout);

        var targetText = targetDocument.Id == document.Id
            ? sourceText
            : await targetDocument.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(sourceText);
        var targetIndent = IndentationOf(targetText, targetBlock.BlockStatement.SpanStart) + "    ";
        var insertionPoint = BeforeEndOfType(targetText, targetBlock);
        var movedBlock = Reindent(source.ToString().TrimEnd(), IndentationOf(sourceText, source.SpanStart),
            targetIndent, newLine);

        if (!edits.TryGetValue(targetDocument.Id, out var targetEdits))
        {
            targetEdits = [];
            edits[targetDocument.Id] = targetEdits;
        }

        targetEdits.Add((new TextSpan(insertionPoint, 0), movedBlock + newLine));
        documents[targetDocument.Id] = targetDocument;

        var texts = new Dictionary<DocumentId, string>();
        foreach (var (documentId, list) in edits)
        {
            var original = (await documents[documentId].GetTextAsync(cancellationToken)).ToString();
            texts[documentId] = RewriteText(original, list.Select(edit => (edit.Span.Start, edit.Span.Length, edit.Text)));
        }

        // ── Gate: the whole solution, because the type's metadata name changed ────────
        var projected = solution;
        foreach (var (documentId, value) in texts)
            projected = projected.WithDocumentText(documentId, SourceText.From(value));
        if (deleteSource) projected = projected.RemoveDocument(document.Id);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected, solution.ProjectIds, cancellationToken);
        if (!gate.Passed)
            return RefactoringCompilerGate.Refusal(gate, $"moving '{typeName}' into '{targetTypeName}'");

        if (!dryRun)
        {
            foreach (var (documentId, value) in texts)
            {
                if (deleteSource && documentId == document.Id) continue;
                if (documents.TryGetValue(documentId, out var edited))
                    await RefactoringFileWriter.WriteAndUpdateAsync(workspace, edited, value, cancellationToken);
            }

            if (deleteSource)
            {
                if (document.FilePath is not null && File.Exists(document.FilePath)) File.Delete(document.FilePath);
                workspace.TryRemoveDocument(document.Id);
            }
        }

        var files = new List<object>();
        foreach (var (documentId, value) in texts)
        {
            if (deleteSource && documentId == document.Id) continue;

            var oldText = documents.TryGetValue(documentId, out var edited)
                ? (await edited.GetTextAsync(cancellationToken)).ToString()
                : string.Empty;

            files.Add(new
            {
                file = documents.TryGetValue(documentId, out var known) ? known.FilePath : null,
                preview = RefactoringFileWriter.Diff(oldText, value),
            });
        }

        return new
        {
            @refactoring = "move_type_to_nested_scope",
            type = sourceSymbol.ToDisplayString(),
            movedInto = targetSymbol.ToDisplayString(),
            file = document.FilePath,
            targetFile = targetDocument.FilePath,
            referencesRewritten = referenceCount,
            sourceFileDeleted = deleteSource,
            filesChanged = texts.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            files,
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>Whether nothing that needs to compile is left in the file.</summary>
    private static bool HasNoDeclarations(string text)
    {
        var root = VisualBasicSyntaxTree.ParseText(text).GetRoot();
        return !root.DescendantNodes().Any(node => node is TypeBlockSyntax or EnumBlockSyntax
            or DelegateStatementSyntax or MethodBlockSyntax);
    }

    // ── shared plumbing ──────────────────────────────────────────────────────

    /// <summary>Applies edits in descending order, so every span stays the one it was computed from.</summary>
    private static string RewriteText(string text, IEnumerable<(int Start, int Length, string Replacement)> edits)
    {
        foreach (var (start, length, replacement) in edits.OrderByDescending(edit => edit.Start)
                     .ThenByDescending(edit => edit.Length))
            text = text[..start] + replacement + text[(start + length)..];

        return text;
    }

    /// <summary>Adds one <c>Imports</c> per touched file that cannot otherwise see the namespace.</summary>
    private static async Task<(List<TextEdit>? Edits, object? Error)> EnsureImportsAsync(Solution solution,
        List<TextEdit> edits, IReadOnlyList<string>? requiredNamespaces, CancellationToken cancellationToken)
    {
        if (requiredNamespaces is null || requiredNamespaces.Count == 0) return (edits, null);

        foreach (var group in edits.GroupBy(edit => edit.Document.Id).ToList())
        {
            var document = group.First().Document;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null) continue;

            var missing = requiredNamespaces
                .Where(value => !string.IsNullOrWhiteSpace(value) && !NamespaceVisible(root, value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missing.Count == 0) continue;
            if (root is not CompilationUnitSyntax unit) return (null, new { error = "Could not read the file's compilation unit." });

            var text = await document.GetTextAsync(cancellationToken);
            var newLine = NewLineOf(text);
            var insertion = TopLevelImportInsertion(unit, text);
            var importText = string.Concat(missing.Select(value => $"Imports {value}{newLine}"));

            edits.Add(new TextEdit(document, new TextSpan(insertion, 0), importText));
        }

        return (edits, null);
    }
}
