using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The declaration-shape refactorings that still stopped at a C# syntax tree, written for Visual Basic
/// with the tool the language gives: a semantic model and text spans. Each operation computes the
/// replacements it can prove, projects them over the solution and asks the compiler gate; nothing is
/// written when the projection adds an error. Shapes the model cannot prove are refused with the reason.
/// </summary>
internal static partial class VbRefactoringTail
{
    private sealed record TextEdit(Document Document, TextSpan Span, string Replacement);

    private sealed record Resolved(Solution Solution, Document Document, SyntaxNode Root, SemanticModel Model);

    private sealed record Resolution(Resolved? Value, object? Error);

    private sealed record Applied(CompilerGate Gate, List<(Document Document, string OldText, string NewText)> Files);

    // ── inline_field ─────────────────────────────────────────────────────────
    public static async Task<object> InlineFieldAsync(IRoslynWorkspace workspace, string filePath, string fieldName,
        string? containingType, bool dryRun, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var declarations = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declarators.Any(declarator =>
                declarator.Names.Any(name => name.Identifier.ValueText == fieldName)))
            .ToList();

        if (declarations.Count == 0) return new { error = $"No field '{fieldName}' in {Path.GetFileName(filePath)}." };
        if (declarations.Count > 1)
            return new { error = $"Several fields named '{fieldName}' in the file; address one type at a time." };

        var declaration = declarations[0];
        if (declaration.Declarators.Count > 1 || declaration.Declarators[0].Names.Count > 1)
            return new { error = "The declaration holds several variables; split it before inlining." };

        var declarator = declaration.Declarators[0];
        var initializer = declarator.Initializer?.Value;
        if (initializer is null)
            return new { error = $"'{fieldName}' has no initializer: inlining it would change what each use evaluates." };

        if (model.GetDeclaredSymbol(declarator.Names[0], cancellationToken) is not IFieldSymbol field)
            return new { error = $"'{fieldName}' did not resolve to a field." };

        if (containingType is not null &&
            !string.Equals(field.ContainingType?.Name, containingType, StringComparison.OrdinalIgnoreCase))
            return new { error = $"'{fieldName}' is declared on '{field.ContainingType?.Name}', not '{containingType}'." };

        var references = await SymbolFinder.FindReferencesAsync(field, solution, cancellationToken);
        var uses = new List<TextSpan>();
        var writes = new List<string>();
        var text = await document.GetTextAsync(cancellationToken);

        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (!string.Equals(location.Document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))
                return new
                {
                    error = $"'{fieldName}' is used in {Path.GetFileName(location.Document.FilePath)} as well: " +
                            "inlining a field across files would paste the initializer where it cannot see its context.",
                    file = location.Document.FilePath,
                };

            if (declaration.Span.Contains(location.Location.SourceSpan)) continue;

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var written = node.AncestorsAndSelf().Any(ancestor =>
                ancestor is AssignmentStatementSyntax assignment && assignment.Left.Span.Contains(node.Span));

            if (written)
            {
                writes.Add($"{Path.GetFileName(document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}");
                continue;
            }

            uses.Add(node.Span);
        }

        if (writes.Count > 0)
            return new { error = $"'{fieldName}' is written outside its declaration ({string.Join(", ", writes)}): " +
                                "inlining would lose the assignment." };

        if (uses.Count == 0)
            return new { error = $"'{fieldName}' has no read outside its declaration: nothing to inline (use safe_delete for that)." };

        var initializerText = "(" + initializer.ToString().Trim() + ")";
        var edits = uses.Distinct().Select(span => new TextEdit(document, span, initializerText)).ToList();
        edits.Add(new TextEdit(document, WholeLines(text, declaration.Span), string.Empty));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, $"inlining '{fieldName}'");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "inline_field",
            field = field.ToDisplayString(),
            uses = edits.Count - 1,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new { file = entry.Document.FilePath, preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText) }).ToList(),
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "Field removed; the initializer is now evaluated at each use.",
        };
    }

    // ── move_to_namespace ────────────────────────────────────────────────────
    public static async Task<object> MoveToNamespaceAsync(IRoslynWorkspace workspace, string filePath,
        string targetNamespace, bool dryRun, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetNamespace))
            return new { error = "The target namespace is required." };

        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var namespaces = root.DescendantNodes().OfType<NamespaceBlockSyntax>().ToList();
        if (namespaces.Count == 0)
            return new { error = $"{Path.GetFileName(filePath)} has no Namespace block; add one (or use move_type) first." };
        if (namespaces.Count > 1)
            return new
            {
                error = $"{Path.GetFileName(filePath)} declares {namespaces.Count} namespaces; this tool moves a file's single namespace.",
                namespaces = namespaces.Select(block => block.NamespaceStatement.Name.ToString()).ToList(),
            };

        var block = namespaces[0];
        var currentNamespace = block.NamespaceStatement.Name.ToString();
        if (string.Equals(currentNamespace, targetNamespace, StringComparison.Ordinal))
            return new { error = $"The file already declares '{targetNamespace}'." };

        var types = block.Members.OfType<TypeBlockSyntax>()
            .Select(type => model.GetDeclaredSymbol(type, cancellationToken))
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .ToList();
        if (types.Count == 0)
            return new { error = $"{Path.GetFileName(filePath)} declares no types directly in its namespace." };

        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var namespaceRemains = compilation is not null && tree is not null && NamespaceRemains(compilation, currentNamespace, tree);

        var edits = new List<TextEdit>
        {
            new(document, block.NamespaceStatement.Name.Span, targetNamespace),
        };

        var referencing = new Dictionary<DocumentId, Document>();
        foreach (var type in types)
            foreach (var reference in await SymbolFinder.FindReferencesAsync(type, solution, cancellationToken))
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                    if (location.Document.Id != document.Id)
                        referencing[location.Document.Id] = location.Document;

        var added = 0;
        var removed = 0;
        foreach (var target in referencing.Values)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            var targetText = await target.GetTextAsync(cancellationToken);
            if (targetRoot is null) continue;

            var imports = targetRoot.DescendantNodes().OfType<ImportsStatementSyntax>().ToList();
            var hasTarget = imports.Any(statement => statement.ImportsClauses
                .OfType<SimpleImportsClauseSyntax>()
                .Any(clause => string.Equals(clause.Name.ToString(), targetNamespace, StringComparison.Ordinal)));

            if (!hasTarget)
            {
                var insertion = ImportInsertion(targetRoot, targetText);
                edits.Add(new TextEdit(target, new TextSpan(insertion, 0), $"Imports {targetNamespace}{Environment.NewLine}"));
                added++;
            }

            if (namespaceRemains) continue;

            // Only a single-clause Imports statement can be removed as a whole; a combined one keeps
            // its other clauses, and the gate refuses the move if the old namespace had to disappear.
            foreach (var statement in imports.Where(statement => statement.ImportsClauses.Count == 1))
            {
                var clause = statement.ImportsClauses[0] as SimpleImportsClauseSyntax;
                if (clause is null || !string.Equals(clause.Name.ToString(), currentNamespace, StringComparison.Ordinal))
                    continue;

                edits.Add(new TextEdit(target, WholeLines(targetText, statement.Span), string.Empty));
                removed++;
            }
        }

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the move");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "move_to_namespace",
            from = currentNamespace,
            to = targetNamespace,
            types = types.Select(type => type.ToDisplayString()).ToList(),
            filesChanged = outcome.Files.Count,
            importsAdded = added,
            importsRemoved = removed,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            dryRun,
            applied = !dryRun,
            files = outcome.Files.Select(entry => new { file = entry.Document.FilePath, preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText) }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── property_to_function ─────────────────────────────────────────────────
    public static async Task<object> PropertyToFunctionAsync(IRoslynWorkspace workspace, string filePath,
        string propertyName, string? containingType, string? getterName, string? setterName, bool dryRun,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(workspace, filePath, cancellationToken);
        if (resolution.Error is not null) return resolution.Error;
        var (solution, document, root, model) = resolution.Value!;

        var candidates = new List<(PropertyStatementSyntax? Statement, PropertyBlockSyntax? Block)>();
        candidates.AddRange(root.DescendantNodes().OfType<PropertyStatementSyntax>()
            .Where(statement => statement.Identifier.ValueText == propertyName && statement.Parent is not PropertyBlockSyntax)
            .Select(statement => ((PropertyStatementSyntax?)statement, (PropertyBlockSyntax?)null)));
        candidates.AddRange(root.DescendantNodes().OfType<PropertyBlockSyntax>()
            .Where(block => block.PropertyStatement.Identifier.ValueText == propertyName)
            .Select(block => ((PropertyStatementSyntax?)null, (PropertyBlockSyntax?)block)));

        if (candidates.Count == 0) return new { error = $"No property '{propertyName}' in {Path.GetFileName(filePath)}." };
        if (candidates.Count > 1)
            return new { error = $"Several properties named '{propertyName}'; the VB path needs one declaration." };

        var candidate = candidates[0];
        var block = candidate.Block;
        var declaration = block is not null ? (SyntaxNode)block : candidate.Statement!;
        var statement = block?.PropertyStatement ?? candidate.Statement!;

        var ownerBlock = declaration.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        if (ownerBlock is null || model.GetDeclaredSymbol(ownerBlock, cancellationToken) is not INamedTypeSymbol owner)
            return new { error = "The property is not a member of a type declaration." };
        if (containingType is not null &&
            !string.Equals(owner.Name, containingType, StringComparison.OrdinalIgnoreCase))
            return new { error = $"'{propertyName}' is declared on '{owner.Name}', not '{containingType}'." };

        if (owner.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault() is not { } property)
            return new { error = $"'{propertyName}' did not resolve to a property." };
        if (property.IsIndexer)
            return new { error = "A default property is not converted here; use function_to_indexer/indexer_to_function by hand." };
        if (property.IsAbstract || property.IsVirtual || property.IsOverride)
            return new { error = $"'{propertyName}' is abstract/virtual/override: every override would have to change too." };

        var typeName = statement.AsClause is SimpleAsClauseSyntax simpleAs ? simpleAs.Type.ToString() : null;
        if (string.IsNullOrWhiteSpace(typeName))
            return new { error = $"'{propertyName}' has no As <type> clause, so its type is not known." };

        var getter = statement.Modifiers.Any(token => token.IsKind(SyntaxKind.WriteOnlyKeyword))
            ? null
            : getterName ?? "Get" + Capitalise(propertyName);
        var setter = statement.Modifiers.Any(token => token.IsKind(SyntaxKind.ReadOnlyKeyword))
            ? null
            : setterName ?? "Set" + Capitalise(propertyName);

        if (getter is not null && !IsValidIdentifier(getter))
            return new { error = $"'{getter}' is not a valid method name; pass getterName explicitly." };
        if (setter is not null && !IsValidIdentifier(setter))
            return new { error = $"'{setter}' is not a valid method name; pass setterName explicitly." };

        var accessorNames = new[] { getter, setter }.Where(name => name is not null).ToList();
        foreach (var name in accessorNames)
            if (owner.GetMembers(name!).Length > 0)
                return new { error = $"'{owner.Name}' already has a member named '{name}'; pass another name." };

        var references = await SymbolFinder.FindReferencesAsync(property, solution, cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        var indentation = IndentationAt(text, declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        var newLine = NewLineOf(text);

        var uses = new List<TextSpan>();
        foreach (var location in references.SelectMany(reference => reference.Locations)
                     .Where(location => location.Location.IsInSource))
        {
            if (declaration.Span.Contains(location.Location.SourceSpan)) continue;

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node.AncestorsAndSelf().Any(ancestor =>
                    ancestor is AssignmentStatementSyntax assignment && assignment.Left.Span.Contains(node.Span)))
                return new
                {
                    error = $"'{propertyName}' is assigned at {Path.GetFileName(document.FilePath)}:" +
                            $"{location.Location.GetLineSpan().StartLinePosition.Line + 1}; the VB path rewrites reads, " +
                            "so a write is refused rather than guessed.",
                };

            if (getter is null)
                return new { error = $"'{propertyName}' has no getter, but it is read." };

            uses.Add(node.Span);
        }

        if (uses.Count == 0)
            return new { error = $"'{propertyName}' has no read: nothing to rewrite (use safe_delete for that)." };

        var modifiers = string.Join(" ", statement.Modifiers.Select(token => token.ToString()).Where(text0 => text0.Length > 0));
        if (modifiers.Length > 0) modifiers += " ";

        var generated = new List<string>();
        if (block is null)
        {
            var backing = "_" + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
            generated.Add($"{indentation}Private {backing} As {typeName}");
            generated.Add(string.Empty);
            if (getter is not null)
                generated.Add($"{indentation}{modifiers}Function {getter}() As {typeName}{newLine}" +
                              $"{indentation}    Return {backing}{newLine}{indentation}End Function");
            if (setter is not null)
                generated.Add($"{indentation}{modifiers}Sub {setter}(value As {typeName}){newLine}" +
                              $"{indentation}    {backing} = value{newLine}{indentation}End Sub");
        }
        else
        {
            var getBody = block.Accessors.FirstOrDefault(accessor =>
                accessor.AccessorStatement.IsKind(SyntaxKind.GetAccessorStatement));
            var setBody = block.Accessors.FirstOrDefault(accessor =>
                accessor.AccessorStatement.IsKind(SyntaxKind.SetAccessorStatement));

            if (getter is not null)
                generated.Add($"{indentation}{modifiers}Function {getter}() As {typeName}{newLine}" +
                              $"{BodyOf(getBody, indentation, newLine)}{newLine}{indentation}End Function");
            if (setter is not null)
                generated.Add($"{indentation}{modifiers}Sub {setter}(value As {typeName}){newLine}" +
                              $"{BodyOf(setBody, indentation, newLine)}{newLine}{indentation}End Sub");
        }

        var replacement = string.Join(newLine, generated);
        var edits = uses.Distinct().Select(span => new TextEdit(document, span, getter + "()")).ToList();
        edits.Add(new TextEdit(document, WholeLines(text, declaration.Span), replacement));

        var applied = await ApplyAsync(workspace, solution, edits, dryRun, cancellationToken, "the conversion");
        if (applied.Error is not null) return applied.Error;
        var outcome = applied.Value!;

        return new
        {
            @refactoring = "property_to_function",
            property = property.ToDisplayString(),
            getter,
            setter,
            file = document.FilePath,
            usages = new Dictionary<string, int> { ["read"] = uses.Count },
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = outcome.Gate.Before, after = outcome.Gate.After },
            files = outcome.Files.Select(entry => new
            {
                file = entry.Document.FilePath,
                preview = RefactoringFileWriter.Diff(entry.OldText, entry.NewText),
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private static string BodyOf(AccessorBlockSyntax? accessor, string indentation, string newLine) =>
        accessor is null || accessor.Statements.Count == 0
            ? string.Empty
            : string.Join(newLine,
                accessor.Statements.Select(statement => indentation + "    " + statement.ToString()));

    private static bool IsValidIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string Capitalise(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static string NewLineOf(SourceText text) =>
        text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string IndentationAt(SourceText text, int line)
    {
        if (line < 1 || line > text.Lines.Count) return string.Empty;
        var lineText = text.ToString(text.Lines[line - 1].Span);
        return lineText[..(lineText.Length - lineText.TrimStart().Length)];
    }

    // ── the shared engine ────────────────────────────────────────────────────
    private static async Task<(Applied? Value, object? Error)> ApplyAsync(IRoslynWorkspace workspace, Solution solution,
        List<TextEdit> edits, bool dryRun, CancellationToken cancellationToken, string what)
    {
        var perDocument = new Dictionary<DocumentId, (Document Document, string OldText, string NewText)>();
        foreach (var group in edits.GroupBy(edit => edit.Document.Id))
        {
            var target = group.First().Document;
            var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
            var text = oldText;
            foreach (var edit in group.OrderByDescending(edit => edit.Span.Start).ThenByDescending(edit => edit.Span.Length))
                text = text[..edit.Span.Start] + edit.Replacement + text[(edit.Span.Start + edit.Span.Length)..];
            perDocument[group.Key] = (target, oldText, text);
        }

        var projected = solution;
        foreach (var (documentId, entry) in perDocument)
            projected = projected.WithDocumentText(documentId, SourceText.From(entry.NewText));

        var gate = await RefactoringCompilerGate.CompareAsync(solution, projected,
            perDocument.Values.Select(entry => entry.Document.Project.Id).Distinct(), cancellationToken);
        if (!gate.Passed) return (null, RefactoringCompilerGate.Refusal(gate, what));

        if (!dryRun)
            foreach (var (_, entry) in perDocument)
                await RefactoringFileWriter.WriteAndUpdateAsync(workspace, entry.Document, entry.NewText, cancellationToken);

        return (new Applied(gate, perDocument.Values.ToList()), null);
    }

    private static async Task<Resolution> ResolveAsync(IRoslynWorkspace workspace, string filePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new Resolution(null, new { error = "A file path is required." });

        var solution = await workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new Resolution(null, new { error = "No solution could be auto-located from the working directory." });

        var document = VbRefactoringSupport.DocumentAt(solution, filePath);
        if (document is null)
            return new Resolution(null, new { error = $"{filePath} is not a document of the loaded solution." });

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
            return new Resolution(null, new { error = $"Could not read the syntax tree of {Path.GetFileName(filePath)}." });

        return new Resolution(new Resolved(solution, document, root, model), null);
    }

    /// <summary>The span of the whole lines a declaration occupies, so removing it leaves no blank line.</summary>
    private static TextSpan WholeLines(SourceText text, TextSpan span)
    {
        var line = text.Lines.GetLineFromPosition(span.Start);
        var before = text.ToString(TextSpan.FromBounds(line.Start, span.Start)).Trim();
        var endLine = text.Lines.GetLineFromPosition(span.End);
        var after = text.ToString(TextSpan.FromBounds(span.End, endLine.End)).Trim();
        return before.Length == 0 && after.Length == 0
            ? TextSpan.FromBounds(line.Start, endLine.EndIncludingLineBreak)
            : span;
    }

    private static int ImportInsertion(SyntaxNode root, SourceText text)
    {
        var lastImport = root.DescendantNodes().OfType<ImportsStatementSyntax>().LastOrDefault();
        var lastOption = root.DescendantNodes().OfType<OptionStatementSyntax>().LastOrDefault();
        var anchor = lastImport?.Span.End ?? lastOption?.Span.End;
        return anchor is null ? 0 : Math.Min(text.Length, anchor.Value + NewLineLength(text, anchor.Value));
    }

    private static int NewLineLength(SourceText text, int position)
    {
        if (position >= text.Length) return 0;
        if (text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n') return 2;
        return text[position] == '\n' ? 1 : 0;
    }

    private static bool NamespaceRemains(Compilation compilation, string name, SyntaxTree moving)
    {
        var symbols = Flatten(compilation.GlobalNamespace)
            .Where(symbol => symbol.ToDisplayString() == name)
            .ToList();

        return symbols.Any(symbol =>
            symbol.GetNamespaceMembers().Any() ||
            symbol.GetTypeMembers().Any(type => type.Locations.Any(location =>
                location.SourceTree is not null && location.SourceTree != moving)));
    }

    private static IEnumerable<INamespaceSymbol> Flatten(INamespaceSymbol root)
    {
        yield return root;
        foreach (var child in root.GetNamespaceMembers())
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
