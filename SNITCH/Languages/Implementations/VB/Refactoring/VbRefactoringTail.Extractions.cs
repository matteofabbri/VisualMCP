using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The declaration-shape operations that end in a new type: a class extracted beside its owner with
/// forwarding members, a base class extracted with an <c>Inherits</c> clause, and the two indexer
/// conversions written in VB's own vocabulary — a <c>Default Property</c> with <c>Get</c>/<c>Set</c>.
/// </summary>
internal static partial class VbRefactoringTail
{
    // ── extract_class ────────────────────────────────────────────────────────

    public static async Task<object> ExtractClassAsync(IRoslynWorkspace workspace, string filePath,
        string typeName, string[] memberNames, string newClassName, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (memberNames is null || memberNames.Length == 0) return new { error = "Name the members to extract." };
        if (!IsValidIdentifier(newClassName)) return new { error = $"'{newClassName}' is not a valid class name." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var type = FindTypeBlock(root, typeName);
        if (type is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(filePath)}." };
        if (model.GetDeclaredSymbol(type.BlockStatement, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        var directory = Path.GetDirectoryName(document.FilePath);
        if (directory is null) return new { error = "The document has no directory on disk." };

        var newFilePath = Path.Combine(directory, newClassName + ".vb");
        if (File.Exists(newFilePath)) return new { error = $"{newFilePath} already exists; pick another name." };
        if (await FindTypeSymbolAsync(solution, newClassName, cancellationToken) is not null)
            return new { error = $"A type '{newClassName}' already exists in the solution; pick another name." };

        var selected = new List<(StatementSyntax Member, ISymbol Symbol)>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var member = type.Members.FirstOrDefault(candidate => MemberName(candidate) == name);
            if (member is null)
                return new
                {
                    error = $"No extractable member '{name}' in '{typeName}' (methods, properties and single-variable fields only).",
                    available = type.Members.Select(MemberName).Where(candidate => candidate is not null).Distinct().ToList(),
                };

            if (HasModifier(ModifiersOf(member), SyntaxKind.SharedKeyword))
                return new { error = $"'{name}' is Shared; a forwarded instance member cannot stand in for it." };

            if (SymbolOfMember(member, model, cancellationToken) is not { } symbol)
                return new { error = $"'{name}' did not resolve." };

            selected.Add((member, symbol));
        }

        var refusal = await RefuseExtractionAsync(solution, type, typeSymbol, selected, model, cancellationToken);
        if (refusal is not null) return new { error = refusal };

        // ── The new class ──────────────────────────────────────────────────────────────
        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var namespaceName = NamespaceOf(typeSymbol);
        var body = string.Join(newLine + newLine, selected.Select(entry =>
            Reindent(entry.Member.ToString().TrimEnd(), IndentationOf(text, entry.Member.SpanStart), "        ", newLine)));

        var classText = (namespaceName is null ? string.Empty : $"Namespace {namespaceName}{newLine}{newLine}") +
                        $"    Friend NotInheritable Class {newClassName}{newLine}{newLine}" +
                        body + newLine + newLine +
                        $"    End Class{newLine}" +
                        (namespaceName is null ? string.Empty : $"End Namespace{newLine}");

        // ── The owner: a holder plus a forwarding member per selection ─────────────────
        var typeIndent = IndentationOf(text, type.BlockStatement.SpanStart);
        var memberIndent = typeIndent + "    ";
        var holderName = "_" + char.ToLowerInvariant(newClassName[0]) + newClassName[1..];

        var edits = new List<TextEdit>
        {
            new(document, new TextSpan(AfterTypeHeader(text, type), 0),
                $"{memberIndent}Private ReadOnly {holderName} As New {newClassName}(){newLine}{newLine}"),
        };

        foreach (var (member, _) in selected)
        {
            if (member is FieldDeclarationSyntax)
            {
                edits.Add(new TextEdit(document, WholeLines(text, member.Span), string.Empty));
                continue;
            }

            var shim = BuildShim(member, holderName, memberIndent, newLine);
            if (shim is null)
                return new
                {
                    error = $"'{MemberName(member)}' cannot be forwarded (only a Sub, a Function and a property with Get/Set accessors).",
                };

            edits.Add(new TextEdit(document, WholeLines(text, member.Span), shim));
        }

        var newText = RewriteText(text.ToString(),
            edits.Select(edit => (edit.Span.Start, edit.Span.Length, edit.Replacement)));

        // ── Gate: the project must still compile, with the new file in place ──────────
        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var added = projected.GetProject(document.Project.Id)!
            .AddDocument(newClassName + ".vb", SourceText.From(classText), filePath: newFilePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, added.Project.Solution,
            [document.Project.Id], cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the extraction");

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);
            await RefactoringFileWriter.WriteAsync(newFilePath, classText, cancellationToken);
            workspace.TryAddDocument(document.Project.Id, newClassName + ".vb", newFilePath, SourceText.From(classText));
        }

        return new
        {
            @refactoring = "extract_class",
            from = typeSymbol.ToDisplayString(),
            to = newClassName,
            newFile = newFilePath,
            members = selected.Select(entry => MemberName(entry.Member)).ToList(),
            forwardingField = $"{newClassName} {holderName}",
            preview = RefactoringFileWriter.Diff(text.ToString(), newText),
            newFilePreview = classText,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>`Function Add(a As Integer)` becomes a body that calls the holder; a field has no shim.</summary>
    private static string? BuildShim(StatementSyntax member, string holderName, string indentation, string newLine)
    {
        var bodyIndent = indentation + "    ";

        switch (member)
        {
            case MethodBlockSyntax method:
            {
                var header = method.SubOrFunctionStatement;
                var arguments = header.ParameterList is null
                    ? string.Empty
                    : string.Join(", ", header.ParameterList.Parameters.Select(parameter =>
                        parameter.Identifier.Identifier.ValueText));

                var call = $"{holderName}.{header.Identifier.ValueText}({arguments})";
                var isSub = header.IsKind(SyntaxKind.SubStatement);

                return string.Join(newLine,
                    indentation + header.ToString(),
                    bodyIndent + (isSub ? call : "Return " + call),
                    indentation + (isSub ? "End Sub" : "End Function")) + newLine;
            }

            case PropertyBlockSyntax property:
            {
                var statement = property.PropertyStatement;
                var typeText = TypeTextOf(statement.AsClause) ?? "Object";
                var getter = property.Accessors.FirstOrDefault(accessor =>
                    accessor.AccessorStatement.IsKind(SyntaxKind.GetAccessorStatement));
                var setter = property.Accessors.FirstOrDefault(accessor =>
                    accessor.AccessorStatement.IsKind(SyntaxKind.SetAccessorStatement));

                return BuildPropertyShim(statement.ToString(), statement.Identifier.ValueText, typeText,
                    getter is not null, setter is not null, holderName, indentation, newLine) + newLine;
            }

            case PropertyStatementSyntax simple:
            {
                var typeText = TypeTextOf(simple.AsClause) ?? "Object";
                var readOnly = HasModifier(simple.Modifiers, SyntaxKind.ReadOnlyKeyword);
                var writeOnly = HasModifier(simple.Modifiers, SyntaxKind.WriteOnlyKeyword);

                return BuildPropertyShim(simple.ToString(), simple.Identifier.ValueText, typeText,
                    !writeOnly, !readOnly, holderName, indentation, newLine) + newLine;
            }

            default:
                return null;
        }
    }

    private static string BuildPropertyShim(string header, string name, string typeText, bool getter, bool setter,
        string holderName, string indentation, string newLine)
    {
        var bodyIndent = indentation + "    ";
        var lines = new List<string> { indentation + header };

        if (getter)
            lines.AddRange([bodyIndent + "Get", bodyIndent + "    Return " + $"{holderName}.{name}",
                bodyIndent + "End Get"]);

        if (setter)
            lines.AddRange([bodyIndent + $"Set(value As {typeText})", bodyIndent + $"    {holderName}.{name} = value",
                bodyIndent + "End Set"]);

        lines.Add(indentation + "End Property");
        return string.Join(newLine, lines);
    }

    // ── extract_superclass ───────────────────────────────────────────────────

    public static async Task<object> ExtractSuperclassAsync(IRoslynWorkspace workspace, string filePath,
        string typeName, string[] memberNames, string baseClassName, bool dryRun,
        CancellationToken cancellationToken)
    {
        if (memberNames is null || memberNames.Length == 0)
            return new { error = "Name the members to move to the base class." };
        if (!IsValidIdentifier(baseClassName)) return new { error = $"'{baseClassName}' is not a valid class name." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var type = root.DescendantNodes().OfType<ClassBlockSyntax>()
            .FirstOrDefault(block => string.Equals(block.BlockStatement.Identifier.ValueText, typeName,
                StringComparison.Ordinal));
        if (type is null)
            return new { error = $"No class '{typeName}' in {Path.GetFileName(filePath)} (only classes get a superclass)." };
        if (model.GetDeclaredSymbol(type.BlockStatement, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        if (typeSymbol.IsStatic) return new { error = $"'{typeName}' is a Module; it cannot derive from a base class." };

        var inherited = type.Members.OfType<InheritsStatementSyntax>().FirstOrDefault();
        if (inherited is not null)
            return new
            {
                error = $"'{typeName}' already inherits '{inherited.Types.FirstOrDefault()}'; Visual Basic allows one base class.",
            };

        var directory = Path.GetDirectoryName(document.FilePath);
        if (directory is null) return new { error = "The document has no directory on disk." };

        var newFilePath = Path.Combine(directory, baseClassName + ".vb");
        if (File.Exists(newFilePath)) return new { error = $"{newFilePath} already exists; pick another name." };
        if (await FindTypeSymbolAsync(solution, baseClassName, cancellationToken) is not null)
            return new { error = $"A type '{baseClassName}' already exists in the solution; pick another name." };

        var selected = new List<(StatementSyntax Member, ISymbol Symbol)>();
        foreach (var name in memberNames.Distinct(StringComparer.Ordinal))
        {
            var member = type.Members.FirstOrDefault(candidate => MemberName(candidate) == name);
            if (member is null)
                return new
                {
                    error = $"No extractable member '{name}' in '{typeName}' (methods, properties and single-variable fields only).",
                    available = type.Members.Select(MemberName).Where(candidate => candidate is not null).Distinct().ToList(),
                };

            if (HasModifier(ModifiersOf(member), SyntaxKind.SharedKeyword))
                return new { error = $"'{name}' is Shared; a static member cannot become an inherited instance member this way." };
            if (HasModifier(ModifiersOf(member), SyntaxKind.OverridesKeyword))
                return new { error = $"'{name}' is Overrides; it cannot move to a class that has nothing to override." };
            if (HasModifier(ModifiersOf(member), SyntaxKind.MustOverrideKeyword))
                return new { error = $"'{name}' is MustOverride; the base class would need it too." };

            if (SymbolOfMember(member, model, cancellationToken) is not { } symbol)
                return new { error = $"'{name}' did not resolve." };

            selected.Add((member, symbol));
        }

        var refusal = await RefuseExtractionAsync(solution, type, typeSymbol, selected, model, cancellationToken);
        if (refusal is not null) return new { error = refusal };

        // ── The base class ─────────────────────────────────────────────────────────────
        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var namespaceName = NamespaceOf(typeSymbol);
        var accessibility = typeSymbol.DeclaredAccessibility == Accessibility.Public ? "Public" : "Friend";
        var mustInherit = typeSymbol.IsAbstract ? " MustInherit" : string.Empty;
        var body = string.Join(newLine + newLine, selected.Select(entry =>
            Reindent(entry.Member.ToString().TrimEnd(), IndentationOf(text, entry.Member.SpanStart), "        ", newLine)));

        var classText = (namespaceName is null ? string.Empty : $"Namespace {namespaceName}{newLine}{newLine}") +
                        $"    {accessibility}{mustInherit} Class {baseClassName}{newLine}{newLine}" +
                        body + newLine + newLine +
                        $"    End Class{newLine}" +
                        (namespaceName is null ? string.Empty : $"End Namespace{newLine}");

        // ── The derived class: members leave, an Inherits clause arrives ──────────────
        var typeIndent = IndentationOf(text, type.BlockStatement.SpanStart);
        var memberIndent = typeIndent + "    ";

        var edits = new List<TextEdit>
        {
            new(document, new TextSpan(AfterTypeHeader(text, type), 0),
                $"{memberIndent}Inherits {baseClassName}{newLine}"),
        };

        foreach (var (member, _) in selected)
            edits.Add(new TextEdit(document, WholeLines(text, member.Span), string.Empty));

        var newText = RewriteText(text.ToString(),
            edits.Select(edit => (edit.Span.Start, edit.Span.Length, edit.Replacement)));

        var projected = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var added = projected.GetProject(document.Project.Id)!
            .AddDocument(baseClassName + ".vb", SourceText.From(classText), filePath: newFilePath);

        var gate = await RefactoringCompilerGate.CompareAsync(solution, added.Project.Solution,
            [document.Project.Id], cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the extraction");

        if (!dryRun)
        {
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);
            await RefactoringFileWriter.WriteAsync(newFilePath, classText, cancellationToken);
            workspace.TryAddDocument(document.Project.Id, baseClassName + ".vb", newFilePath, SourceText.From(classText));
        }

        return new
        {
            @refactoring = "extract_superclass",
            from = typeSymbol.ToDisplayString(),
            baseClass = baseClassName,
            newFile = newFilePath,
            members = selected.Select(entry => MemberName(entry.Member)).ToList(),
            preview = RefactoringFileWriter.Diff(text.ToString(), newText),
            newFilePreview = classText,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    /// <summary>
    /// The selection has to be self-contained: a member that travels may only read members that travel
    /// with it, a field cannot be forwarded, and a constructor that stays behind may not assign one.
    /// </summary>
    private static async Task<string?> RefuseExtractionAsync(Solution solution, TypeBlockSyntax type,
        INamedTypeSymbol typeSymbol, List<(StatementSyntax Member, ISymbol Symbol)> selected, SemanticModel model,
        CancellationToken cancellationToken)
    {
        var selectedSymbols = new HashSet<ISymbol>(
            selected.Select(entry => entry.Symbol), SymbolEqualityComparer.Default);

        var movedFields = selected.Select(entry => entry.Member).OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declarators)
            .SelectMany(declarator => declarator.Names)
            .Select(name => name.Identifier.ValueText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var constructor in type.Members.OfType<ConstructorBlockSyntax>())
            foreach (var assignment in constructor.DescendantNodes().OfType<AssignmentStatementSyntax>())
            {
                var target = assignment.Left switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax { Expression: MeExpressionSyntax } access => access.Name.Identifier.ValueText,
                    _ => null,
                };

                if (target is not null && movedFields.Contains(target))
                    return $"Field '{target}' is assigned in a constructor that stays behind; initialise it in the new type instead.";
            }

        foreach (var (member, _) in selected)
            foreach (var identifier in member.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
                if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol)) continue;
                if (symbol.IsStatic) continue;
                if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, typeSymbol)) continue;
                if (selectedSymbols.Contains(symbol)) continue;

                return $"'{MemberName(member)}' reads '{symbol.Name}', which is not being extracted. " +
                       "Add it to the selection or leave the member where it is.";
            }

        foreach (var declarator in selected.Select(entry => entry.Member).OfType<FieldDeclarationSyntax>()
                     .SelectMany(field => field.Declarators))
        {
            if (model.GetDeclaredSymbol(declarator.Names[0], cancellationToken) is not IFieldSymbol field) continue;

            var references = await SymbolFinder.FindReferencesAsync(field, solution, cancellationToken);
            foreach (var location in references.SelectMany(reference => reference.Locations)
                         .Where(location => location.Location.IsInSource))
            {
                var span = location.Location.SourceSpan;
                if (selected.Any(entry => entry.Member.Span.Contains(span))) continue;
                if (span.IntersectsWith(declarator.Names[0].Span)) continue;

                var where = type.Span.Contains(span)
                    ? "a member that stays behind"
                    : $"code outside '{typeSymbol.Name}'";

                return $"Field '{declarator.Names[0].Identifier.ValueText}' is read by {where} " +
                       $"({Path.GetFileName(location.Document.FilePath)}:" +
                       $"{location.Location.GetLineSpan().StartLinePosition.Line + 1}); a field cannot be inherited privately.";
            }
        }

        return null;
    }

    // ── function_to_indexer ──────────────────────────────────────────────────

    public static async Task<object> FunctionToIndexerAsync(IRoslynWorkspace workspace, string filePath,
        string methodName, string? containingType, bool dryRun, CancellationToken cancellationToken)
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
        if (candidates.Count == 0) return new { error = $"No method '{methodName}' in the file." };
        if (candidates.Count > 1) return new { error = $"'{methodName}' has overloads; convert one at a time." };

        var method = candidates[0];
        var header = method.SubOrFunctionStatement;
        if (model.GetDeclaredSymbol(header, cancellationToken) is not IMethodSymbol methodSymbol)
            return new { error = $"'{methodName}' did not resolve to a method." };

        if (methodSymbol.IsStatic) return new { error = $"'{methodName}' is Shared; a default property cannot be Shared." };
        if (methodSymbol.IsAbstract || methodSymbol.IsVirtual || methodSymbol.IsOverride)
            return new { error = $"'{methodName}' is MustOverride/Overridable/Overrides; its contract would break." };

        var parameters = header.ParameterList?.Parameters ?? default;
        var isSetter = header.IsKind(SyntaxKind.SubStatement);
        string typeText;
        string indexText;
        string? valueType = null;

        if (isSetter)
        {
            if (parameters.Count == 0 ||
                !string.Equals(parameters[^1].Identifier.Identifier.ValueText, "value", StringComparison.Ordinal))
                return new { error = "A Sub becomes an indexer setter only when its last parameter is named 'value'." };

            valueType = TypeTextOf(parameters[^1].AsClause) ?? "Object";
            typeText = valueType;
            indexText = string.Join(", ", parameters.Take(parameters.Count - 1).Select(parameter => parameter.ToString()));
        }
        else
        {
            if (parameters.Count == 0)
                return new { error = "A method with no parameters cannot become an indexer; use function_to_property." };

            typeText = TypeTextOf(header.AsClause) ?? "Object";
            indexText = string.Join(", ", parameters.Select(parameter => parameter.ToString()));
        }

        // ── Usages must be plain calls in this file ────────────────────────────────────
        var invocations = new List<InvocationExpressionSyntax>();
        var unsupported = new List<string>();

        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, cancellationToken);
        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (location.Document.Id == document.Id && method.Span.Contains(location.Location.SourceSpan)) continue;

            if (!string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))
                return new
                {
                    error = $"'{methodName}' is called from {Path.GetFileName(location.Document.FilePath)}; " +
                            "indexer call sites must be in the same file as the declaration.",
                };

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Expression.Span.Contains(node.Span));
            if (invocation is null)
            {
                unsupported.Add($"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                continue;
            }

            if (method.Span.Contains(invocation.Span))
                return new { error = $"'{methodName}' calls itself; the recursive call would keep the old name." };

            invocations.Add(invocation);
        }

        if (unsupported.Count > 0)
            return new { error = $"{unsupported.Count} use(s) are not plain calls.", uses = unsupported };

        // ── The property text ──────────────────────────────────────────────────────────
        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var indent = IndentationOf(text, header.SpanStart);
        var bodyIndent = indent + "    ";

        var modifiers = string.Join(" ", header.Modifiers.Select(token => token.ToString())
            .Where(token => token.Length > 0 && !string.Equals(token, "Shared", StringComparison.OrdinalIgnoreCase)));
        if (modifiers.Length > 0) modifiers += " ";

        var lines = new List<string>
        {
            $"{indent}Default {modifiers}{(isSetter ? "WriteOnly" : "ReadOnly")} Property Item({indexText}) As {typeText}",
        };

        if (!isSetter)
        {
            lines.Add(bodyIndent + "Get");
            lines.AddRange(method.Statements.Select(statement => bodyIndent + "    " + statement.ToString()));
            lines.Add(bodyIndent + "End Get");
        }
        else
        {
            lines.Add(bodyIndent + $"Set(value As {valueType})");
            lines.AddRange(method.Statements.Select(statement => bodyIndent + "    " + statement.ToString()));
            lines.Add(bodyIndent + "End Set");
        }

        lines.Add(indent + "End Property");

        var edits = new List<TextEdit>();
        foreach (var invocation in invocations)
        {
            var receiver = invocation.Expression is MemberAccessExpressionSyntax access
                ? access.Expression.ToString()
                : "Me";

            var arguments = invocation.ArgumentList.Arguments.ToList();
            if (isSetter)
            {
                if (invocation.Parent is not ExpressionStatementSyntax statement || arguments.Count == 0)
                {
                    unsupported.Add($"line {invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    continue;
                }

                var indexes = string.Join(", ", arguments.Take(arguments.Count - 1).Select(argument => argument.ToString()));
                edits.Add(new TextEdit(document, statement.Span,
                    $"{receiver}({indexes}) = {arguments[^1].ToString()}"));
            }
            else
            {
                var indexes = string.Join(", ", arguments.Select(argument => argument.ToString()));
                edits.Add(new TextEdit(document, invocation.Span, $"{receiver}({indexes})"));
            }
        }

        if (unsupported.Count > 0)
            return new { error = $"{unsupported.Count} use(s) are not plain calls.", uses = unsupported };

        edits.Add(new TextEdit(document, WholeLines(text, method.Span), string.Join(newLine, lines)));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the conversion");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "function_to_indexer",
            method = methodSymbol.ToDisplayString(),
            property = "Item",
            callSites = invocations.Count,
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

    // ── indexer_to_function ──────────────────────────────────────────────────

    public static async Task<object> IndexerToFunctionAsync(IRoslynWorkspace workspace, string filePath,
        string? containingType, string? getterName, string? setterName, bool dryRun,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var properties = root.DescendantNodes().OfType<PropertyBlockSyntax>()
            .Where(block => HasModifier(block.PropertyStatement.Modifiers, SyntaxKind.DefaultKeyword))
            .Where(block => containingType is null || string.Equals(
                model.GetDeclaredSymbol(block.PropertyStatement, cancellationToken)?.ContainingType?.Name,
                containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (properties.Count == 0) return new { error = "No default property (indexer) in the file." };
        if (properties.Count > 1)
            return new { error = "The file declares several default properties; pass containingType." };

        var property = properties[0];
        if (model.GetDeclaredSymbol(property.PropertyStatement, cancellationToken) is not IPropertySymbol symbol)
            return new { error = "The default property did not resolve." };

        var getter = property.Accessors.FirstOrDefault(accessor =>
            accessor.AccessorStatement.IsKind(SyntaxKind.GetAccessorStatement));
        var setter = property.Accessors.FirstOrDefault(accessor =>
            accessor.AccessorStatement.IsKind(SyntaxKind.SetAccessorStatement));
        if (getter is null && setter is null) return new { error = "The property has no accessor to convert." };

        var getName = getter is null ? null : getterName ?? "Get";
        var setName = setter is null ? null : setterName ?? "Set";

        var owner = property.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (owner is null) return new { error = "The property is not a member of a type declaration." };

        foreach (var name in new[] { getName, setName }.Where(name => name is not null))
        {
            if (!IsValidIdentifier(name!)) return new { error = $"'{name}' is not a valid method name." };
            if (owner.Members.Any(member => string.Equals(MemberName(member), name, StringComparison.Ordinal)))
                return new { error = $"'{owner.BlockStatement.Identifier.ValueText}' already has a member named '{name}'." };
        }

        // ── A default property has no name token at its call sites: scan the file ─────
        var accesses = new List<InvocationExpressionSyntax>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (property.Span.Contains(invocation.Span)) continue;
            if (!SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(invocation, cancellationToken).Symbol, symbol))
                continue;

            accesses.Add(invocation);
        }

        foreach (var project in solution.Projects)
            foreach (var candidate in project.Documents)
            {
                if (candidate.Id == document.Id) continue;

                var candidateRoot = await candidate.GetSyntaxRootAsync(cancellationToken);
                var candidateModel = await candidate.GetSemanticModelAsync(cancellationToken);
                if (candidateRoot is null || candidateModel is null) continue;

                var used = candidateRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(invocation => SymbolEqualityComparer.Default.Equals(
                        candidateModel.GetSymbolInfo(invocation, cancellationToken).Symbol, symbol));

                if (used)
                    return new
                    {
                        error = $"The default property is also used in {Path.GetFileName(candidate.FilePath)}; " +
                                "indexer call sites must be in the same file as the declaration.",
                    };
            }

        var text = await document.GetTextAsync(cancellationToken);
        var newLine = NewLineOf(text);
        var indent = IndentationOf(text, property.PropertyStatement.SpanStart);
        var bodyIndent = indent + "    ";
        var typeText = TypeTextOf(property.PropertyStatement.AsClause) ?? "Object";
        var parameterText = property.PropertyStatement.ParameterList is null
            ? string.Empty
            : string.Join(", ", property.PropertyStatement.ParameterList.Parameters.Select(parameter => parameter.ToString()));

        var modifiers = string.Join(" ", property.PropertyStatement.Modifiers.Select(token => token.ToString())
            .Where(token => token.Length > 0 &&
                            !string.Equals(token, "Default", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(token, "ReadOnly", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(token, "WriteOnly", StringComparison.OrdinalIgnoreCase)));
        if (modifiers.Length > 0) modifiers += " ";

        var methods = new List<string>();
        if (getter is not null && getName is not null)
        {
            var lines = new List<string> { $"{indent}{modifiers}Function {EscapeIdentifier(getName)}({parameterText}) As {typeText}" };
            lines.AddRange(getter.Statements.Select(statement => bodyIndent + statement.ToString()));
            lines.Add(indent + "End Function");
            methods.Add(string.Join(newLine, lines));
        }

        if (setter is not null && setName is not null)
        {
            var separator = parameterText.Length > 0 ? ", " : string.Empty;
            var lines = new List<string> { $"{indent}{modifiers}Sub {EscapeIdentifier(setName)}({parameterText}{separator}value As {typeText})" };
            lines.AddRange(setter.Statements.Select(statement => bodyIndent + statement.ToString()));
            lines.Add(indent + "End Sub");
            methods.Add(string.Join(newLine, lines));
        }

        if (methods.Count == 0) return new { error = "Could not build the replacement methods." };

        var edits = new List<TextEdit>();
        foreach (var invocation in accesses)
        {
            var receiver = invocation.Expression is MemberAccessExpressionSyntax access
                ? access.Expression.ToString()
                : "Me";

            var arguments = invocation.ArgumentList.Arguments.ToList();

            if (invocation.Parent is AssignmentStatementSyntax assignment && assignment.Left.Span.Contains(invocation.Span))
            {
                if (setName is null)
                    return new { error = "The property is written but has no Set accessor to convert." };

                var written = string.Join(", ", arguments.Select(argument => argument.ToString()));
                var separator = written.Length > 0 ? ", " : string.Empty;
                edits.Add(new TextEdit(document, assignment.Span,
                    $"{receiver}.{EscapeIdentifier(setName)}({written}{separator}{assignment.Right.ToString()})"));
            }
            else
            {
                if (getName is null)
                    return new { error = "The property is read but has no Get accessor to convert." };

                var read = string.Join(", ", arguments.Select(argument => argument.ToString()));
                edits.Add(new TextEdit(document, invocation.Span, $"{receiver}.{EscapeIdentifier(getName)}({read})"));
            }
        }

        edits.Add(new TextEdit(document, WholeLines(text, property.Span), string.Join(newLine + newLine, methods)));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the conversion");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "indexer_to_function",
            property = symbol.ToDisplayString(),
            getter = getName,
            setter = setName,
            callSites = accesses.Count,
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
