using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>Rider's "To auto-property" (Property2Auto): trivial accessors over one backing field become <c>{ get; set; }</c>.</summary>
public interface IPropertyToAutoService
{
    Task<object> PropertyToAuto(
        string filePath,
        string propertyName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class PropertyToAutoService : IPropertyToAutoService
{
    private readonly IRoslynWorkspace _workspace;

    public PropertyToAutoService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> PropertyToAuto(
        string filePath,
        string propertyName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(propertyName)) return new { error = "A property name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is not null &&
                                 string.Equals(Path.GetFullPath(d.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        // Writing does not update the in-memory solution (the workspace has no mutation API), so a
        // second refactoring would rewrite the file from the old text and silently undo the first.
        // Refuse instead of corrupting the file.
        var diskText = await File.ReadAllTextAsync(path, cancellationToken);
        var modelledText = (await document.GetTextAsync(cancellationToken)).ToString();
        if (!string.Equals(diskText, modelledText, StringComparison.Ordinal))
            return new
            {
                error = $"{Path.GetFileName(path)} changed on disk after the solution was loaded; the in-memory model is stale. " +
                        "Call load_solution, then retry.",
            };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var candidates = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(declaration => declaration.Identifier.ValueText == propertyName)
            .Where(declaration => containingType is null ||
                                  string.Equals(model.GetDeclaredSymbol(declaration, cancellationToken)?.ContainingType?.Name,
                                                containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            return new { error = $"No property '{propertyName}' in {Path.GetFileName(path)}{(containingType is null ? "" : $" of type '{containingType}'")}." };
        if (candidates.Count > 1)
            return new { error = $"Several properties named '{propertyName}' in the file; pass containingType.", types = candidates.Select(c => model.GetDeclaredSymbol(c, cancellationToken)?.ContainingType?.ToDisplayString()).ToList() };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IPropertySymbol property)
            return new { error = $"'{propertyName}' did not resolve to a property symbol." };

        // ── 1. Recover the backing field from the accessor bodies ───────────────────────────
        IdentifierNameSyntax? getterField;
        AccessorDeclarationSyntax? setter = null;
        var accessors = declaration.AccessorList?.Accessors;

        if (declaration.ExpressionBody is not null)
        {
            getterField = declaration.ExpressionBody.Expression as IdentifierNameSyntax;
            if (getterField is null)
                return new { error = "The property is expression-bodied but does not simply return a field; nothing to simplify." };
        }
        else
        {
            if (accessors is null) return new { error = "The property has no accessor list." };

            var attributed = accessors.Value.FirstOrDefault(a => a.AttributeLists.Count > 0);
            if (attributed is not null)
                return new { error = $"The {attributed.Keyword.ValueText} accessor carries attributes; an auto-property cannot keep them." };

            var getter = accessors.Value.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            if (getter is null) return new { error = "The property has no getter." };

            getterField = GetterField(getter);
            if (getterField is null)
                return new { error = "The getter body is not a single 'return <field>;'; nothing to simplify." };

            setter = accessors.Value.FirstOrDefault(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));
            if (setter is not null)
            {
                var setterField = SetterField(setter);
                if (setterField is null)
                    return new { error = "The setter body is not a single '<field> = value;'; nothing to simplify." };
                if (setterField.Identifier.ValueText != getterField.Identifier.ValueText)
                    return new { error = "The getter and the setter use different fields." };
            }

            var unexpected = accessors.Value.FirstOrDefault(a =>
                !a.IsKind(SyntaxKind.GetAccessorDeclaration) &&
                !a.IsKind(SyntaxKind.SetAccessorDeclaration) &&
                !a.IsKind(SyntaxKind.InitAccessorDeclaration));
            if (unexpected is not null)
                return new { error = $"The property has an unexpected {unexpected.Keyword.ValueText} accessor." };
        }

        var fieldName = getterField.Identifier.ValueText;

        // ── 2. The field must live in this file and be used nowhere else ────────────────────
        var fieldDeclaration = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName));
        if (fieldDeclaration is null)
            return new { error = $"Backing field '{fieldName}' is not declared in {Path.GetFileName(path)}; refusing to guess." };

        var variable = fieldDeclaration.Declaration.Variables.First(v => v.Identifier.ValueText == fieldName);
        if (model.GetDeclaredSymbol(variable, cancellationToken) is not IFieldSymbol fieldSymbol)
            return new { error = $"'{fieldName}' did not resolve to a field symbol." };

        var outside = new List<string>();
        var references = await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken);
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations.Where(l => l.Location.IsInSource))
            {
                if (IsAccountedFor(location, document, declaration, variable)) continue;
                var span = location.Location.GetLineSpan();
                outside.Add($"{Path.GetFileName(location.Document.FilePath)}:{span.StartLinePosition.Line + 1}");
            }
        }

        if (outside.Count > 0)
            return new
            {
                error = $"Backing field '{fieldName}' is used in {outside.Count} place(s) outside the property; " +
                        "turning it into an auto-property would leave them disconnected.",
                uses = outside.Take(10).ToList(),
            };

        // ── 3. Rebuild the property and drop the field ──────────────────────────────────────
        var initializer = variable.Initializer?.Value;
        var autoProperty = BuildAutoProperty(declaration, setter, initializer);

        // When the field was the first member and the property follows it, the property inherits the
        // "first member" position — and must inherit the field's leading trivia with it, or the blank
        // line that separated the two members is left dangling right under the opening brace.
        var owner = declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var inheritsFirstSlot = owner is not null &&
                                owner.Members.IndexOf(fieldDeclaration) == 0 &&
                                owner.Members.IndexOf(declaration) == 1;
        if (inheritsFirstSlot)
            autoProperty = autoProperty.WithLeadingTrivia(fieldDeclaration.GetLeadingTrivia());

        var newRoot = root.ReplaceNode(declaration, autoProperty);
        var fieldToRemove = newRoot.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName));
        if (fieldToRemove is not null)
            newRoot = newRoot.RemoveNode(fieldToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun && document.FilePath is not null)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "property_to_auto",
            file = document.FilePath,
            property = property.Name,
            field = fieldName,
            removalMovedInitializer = initializer is not null,
            dryRun,
            applied = !dryRun,
            changedLines = new { oldLines = CountLines(oldText), newLines = CountLines(newText) },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private static bool IsAccountedFor(
        ReferenceLocation location, Document document, PropertyDeclarationSyntax property, VariableDeclaratorSyntax variable)
    {
        if (location.Document.Id != document.Id) return false;

        var span = location.Location.SourceSpan;
        if (property.Span.Contains(span)) return true;
        return variable.Identifier.Span.Contains(span);
    }

    private static IdentifierNameSyntax? GetterField(AccessorDeclarationSyntax getter)
    {
        if (getter.ExpressionBody is not null)
            return getter.ExpressionBody.Expression as IdentifierNameSyntax;

        var statements = getter.Body?.Statements;
        if (statements is { Count: 1 } && statements.Value[0] is ReturnStatementSyntax { Expression: IdentifierNameSyntax field })
            return field;

        return null;
    }

    private static IdentifierNameSyntax? SetterField(AccessorDeclarationSyntax setter)
    {
        if (setter.ExpressionBody?.Expression is AssignmentExpressionSyntax assignment)
            return assignment.Left as IdentifierNameSyntax;

        var statements = setter.Body?.Statements;
        if (statements is { Count: 1 } &&
            statements.Value[0] is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax statement })
        {
            return statement.Left as IdentifierNameSyntax;
        }

        return null;
    }

    private static PropertyDeclarationSyntax BuildAutoProperty(
        PropertyDeclarationSyntax declaration,
        AccessorDeclarationSyntax? setter,
        ExpressionSyntax? initializer)
    {
        var accessors = new List<AccessorDeclarationSyntax>
        {
            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
        };

        if (setter is not null)
        {
            var keyword = setter.IsKind(SyntaxKind.InitAccessorDeclaration)
                ? SyntaxKind.InitAccessorDeclaration
                : SyntaxKind.SetAccessorDeclaration;

            accessors.Add(AccessorDeclaration(keyword)
                .WithModifiers(setter.Modifiers)
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));
        }

        var property = declaration
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(AccessorList(List(accessors)));

        if (initializer is not null)
        {
            property = property
                .WithInitializer(EqualsValueClause(initializer))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        // The accessors were rebuilt from scratch, so their trivia is gone: normalise the node and
        // then restore the original indentation instead of emitting "public int X{get;set;}=3;".
        return ((PropertyDeclarationSyntax)property.NormalizeWhitespace())
            .WithLeadingTrivia(declaration.GetLeadingTrivia())
            .WithTrailingTrivia(declaration.GetTrailingTrivia());
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Split('\n').Length;
}
