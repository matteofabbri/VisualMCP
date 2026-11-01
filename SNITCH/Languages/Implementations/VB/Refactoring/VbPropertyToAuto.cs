using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "To auto-property" for Visual Basic: a property whose <c>Get</c> returns a field and whose
/// <c>Set</c> assigns it becomes <c>Public Property X As T</c> and the backing field is dropped, as long
/// as the field is used nowhere else — a field read from other members would silently stop tracking the
/// property.
/// </summary>
internal static class VbPropertyToAuto
{
    public static async Task<object> RunAsync(
        IRoslynWorkspace workspace,
        Solution solution,
        Document document,
        string propertyName,
        string? containingType,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (document.FilePath is null) return new { error = "The document has no file on disk." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(document.FilePath)}." };

        var candidates = root.DescendantNodes().OfType<PropertyBlockSyntax>()
            .Where(property => property.PropertyStatement.Identifier.ValueText == propertyName)
            .Where(property => containingType is null ||
                               string.Equals(model.GetDeclaredSymbol(property.PropertyStatement, cancellationToken)?.ContainingType?.Name,
                                   containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new
            {
                error = $"No property '{propertyName}' in {Path.GetFileName(document.FilePath)}" +
                        (containingType is null ? "." : $" of type '{containingType}'."),
            };

        if (candidates.Count > 1)
            return new
            {
                error = $"Several properties named '{propertyName}' in the file; pass containingType.",
                types = candidates
                    .Select(property => model.GetDeclaredSymbol(property.PropertyStatement, cancellationToken)?.ContainingType?.ToDisplayString())
                    .ToList(),
            };

        var property = candidates[0];
        var statement = property.PropertyStatement;

        if (statement.AttributeLists.Count > 0)
            return new { error = "The property carries attributes; an auto-property cannot keep the accessor bodies they describe." };
        if (statement.ParameterList is not null)
            return new { error = "The property takes parameters (a default property); an auto-property cannot." };

        var trivial = TrivialAccessors(property, model, cancellationToken);
        if (trivial.Error is not null) return new { error = trivial.Error };

        var field = trivial.Field!;
        var fieldDeclaration = FieldDeclarationOf(field, cancellationToken);
        if (fieldDeclaration is null || fieldDeclaration.SyntaxTree != property.SyntaxTree)
            return new
            {
                error = $"Backing field '{field.Name}' is not declared in {Path.GetFileName(document.FilePath)}; refusing to guess.",
            };

        if (fieldDeclaration.Declarators.Count != 1 || fieldDeclaration.Declarators[0].Names.Count != 1)
            return new
            {
                error = $"Backing field '{field.Name}' shares its declaration with another field; split the declaration first.",
            };

        // ── the field must belong to this property alone ───────────────────────────────────────
        var declarator = fieldDeclaration.Declarators[0];
        var outside = new List<string>();

        foreach (var reference in await SymbolFinder.FindReferencesAsync(field, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (location.Document.Id != document.Id)
                {
                    outside.Add($"{Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}");
                    continue;
                }

                var span = location.Location.SourceSpan;
                if (property.Span.Contains(span)) continue;
                if (declarator.Names[0].Span.Contains(span)) continue;

                outside.Add($"{Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}");
            }
        }

        if (outside.Count > 0)
            return new
            {
                error = $"Backing field '{field.Name}' is used in {outside.Count} place(s) outside the property; " +
                        "turning it into an auto-property would leave them disconnected.",
                uses = outside.Take(10).ToList(),
            };

        // ── the two edits: the property becomes an auto-property, the field goes away ─────────
        var text = await document.GetTextAsync(cancellationToken);
        var declaration = statement.Modifiers.Count == 0
            ? $"Property {statement.Identifier.ValueText} As {trivial.Type}"
            : $"{statement.Modifiers} Property {statement.Identifier.ValueText} As {trivial.Type}";

        if (trivial.Initializer is not null)
            declaration += " " + trivial.Initializer;

        var changes = new List<TextChange>
        {
            new(property.Span, declaration),
            new(LineSpanOf(text, fieldDeclaration), string.Empty),
        };

        var newText = text.WithChanges(changes).ToString();
        var after = solution.WithDocumentText(document.Id, SourceText.From(newText, text.Encoding));
        var gate = await RefactoringCompilerGate.CompareAsync(solution, after, [document.Project.Id], cancellationToken);

        if (!gate.Passed)
            return new
            {
                error = $"Refused: turning '{propertyName}' into an auto-property would add {gate.Added} compiler " +
                        $"error(s) ({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
            };

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "property_to_auto",
            language = "Visual Basic",
            file = document.FilePath,
            property = propertyName,
            field = field.Name,
            removalMovedInitializer = trivial.Initializer is not null,
            dryRun,
            applied = !dryRun,
            compilerErrors = new { before = gate.Before, after = gate.After },
            preview = RefactoringFileWriter.Diff(text.ToString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>
    /// The property whose <c>Get</c> is "Return field" and whose <c>Set</c> is "field = value", with the
    /// field's declaration and initializer, or the reason it is not one.
    /// </summary>
    private static (IFieldSymbol? Field, EqualsValueSyntax? Initializer, string Type, string? Error) TrivialAccessors(
        PropertyBlockSyntax property, SemanticModel model, CancellationToken cancellationToken)
    {
        var statement = property.PropertyStatement;

        if (property.Accessors.Count != 2)
            return (null, null, string.Empty, "The property does not have exactly a Get and a Set accessor.");

        var getter = property.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorBlock));
        var setter = property.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.SetAccessorBlock));
        if (getter is null || setter is null)
            return (null, null, string.Empty, "The property has no Get/Set pair; only a read-write property can become an auto-property.");

        if (getter.Statements.Count != 1)
            return (null, null, string.Empty, "The Get accessor is not a single 'Return <field>'.");
        if (getter.Statements[0] is not ReturnStatementSyntax { Expression: IdentifierNameSyntax read })
            return (null, null, string.Empty, "The Get accessor is not a single 'Return <field>'.");

        if (model.GetSymbolInfo(read, cancellationToken).Symbol is not IFieldSymbol field)
            return (null, null, string.Empty, "The Get accessor does not return a field.");

        if (setter.Statements.Count != 1 ||
            setter.Statements[0] is not AssignmentStatementSyntax assignment ||
            !assignment.IsKind(SyntaxKind.SimpleAssignmentStatement) ||
            assignment.Left is not IdentifierNameSyntax written ||
            !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(written, cancellationToken).Symbol, field) ||
            assignment.Right is not IdentifierNameSyntax value ||
            !string.Equals(value.Identifier.ValueText, "value", StringComparison.Ordinal))
            return (null, null, string.Empty, "The Set accessor is not a single '<field> = value'.");

        if (statement.AsClause is not SimpleAsClauseSyntax asClause)
            return (null, null, string.Empty, "The property declares no type; an auto-property needs one.");

        if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(asClause.Type, cancellationToken).Type, field.Type))
            return (null, null, string.Empty, "The property and its backing field have different types.");

        if (statement.ImplementsClause is not null)
            return (null, null, string.Empty, "The property implements an interface member; keep the accessors explicit.");

        var declaration = FieldDeclarationOf(field, cancellationToken);
        if (declaration is null) return (null, null, string.Empty, "The backing field declaration could not be located.");

        return (field, declaration.Declarators[0].Initializer, asClause.Type.ToString(), null);
    }

    /// <summary>
    /// The field declaration a field symbol came from. VB points the reference at the declarator, not at
    /// the declaration statement, so the answer is the nearest <c>FieldDeclarationSyntax</c> above it.
    /// </summary>
    private static FieldDeclarationSyntax? FieldDeclarationOf(IFieldSymbol field, CancellationToken cancellationToken)
    {
        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(cancellationToken);
            var declaration = syntax as FieldDeclarationSyntax ?? syntax.FirstAncestorOrSelf<FieldDeclarationSyntax>();
            if (declaration is not null) return declaration;
        }

        return null;
    }

    /// <summary>The whole line the declaration occupies, so removing it leaves no blank line behind.</summary>
    private static TextSpan LineSpanOf(SourceText text, SyntaxNode node)
    {
        var start = text.Lines.GetLineFromPosition(node.SpanStart).Start;
        var end = text.Lines.GetLineFromPosition(node.Span.End);
        return TextSpan.FromBounds(start, Math.Min(end.EndIncludingLineBreak, text.Length));
    }
}
