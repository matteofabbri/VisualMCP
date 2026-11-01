using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Inline class": a helper class that exists only as one field of its owner has its members
/// pushed into the owner, the accesses lose the field prefix, and the helper disappears. Every other use
/// of the type or of the field is a refusal, because inlining would leave it dangling.
/// </summary>
public interface IInlineClassService
{
    Task<object> InlineClass(
        string filePath,
        string ownerTypeName,
        string fieldName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class InlineClassService : IInlineClassService
{
    private readonly IRoslynWorkspace _workspace;

    public InlineClassService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InlineClass(
        string filePath,
        string ownerTypeName,
        string fieldName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(ownerTypeName)) return new { error = "The owner type is required." };
        if (string.IsNullOrWhiteSpace(fieldName)) return new { error = "The field that holds the class is required." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.InlineClassAsync(
                _workspace, filePath, ownerTypeName, fieldName, dryRun, cancellationToken);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = await FindDocumentAsync(solution, path);
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var owner = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == ownerTypeName);
        if (owner is null) return new { error = $"No type '{ownerTypeName}' in {Path.GetFileName(path)}." };

        var field = owner.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Declaration.Variables.Any(variable => variable.Identifier.ValueText == fieldName));
        if (field is null) return new { error = $"No field '{fieldName}' in '{ownerTypeName}'." };
        if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
            return new { error = $"'{fieldName}' is static; only an instance field can be inlined into its owner." };
        if (field.Declaration.Variables.Count != 1)
            return new { error = $"'{fieldName}' is declared with other variables; split the declaration first." };

        var variable = field.Declaration.Variables[0];
        if (variable.Initializer?.Value is not ObjectCreationExpressionSyntax creation ||
            creation.ArgumentList is not { Arguments.Count: 0 } || creation.Initializer is not null)
            return new { error = $"'{fieldName}' must be initialised with `new Helper()` (no arguments, no object initializer)." };

        if (model.GetSymbolInfo(creation, cancellationToken).Symbol is not IMethodSymbol constructorSymbol)
            return new { error = $"'{variable.Initializer.Value}' did not resolve to a constructor." };

        var helper = constructorSymbol.ContainingType;
        if (helper is null) return new { error = "The field's type did not resolve." };

        var helperRefusal = RefuseHelper(helper, field);
        if (helperRefusal is not null) return new { error = helperRefusal };

        var helperDeclaration = helper.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();
        if (helperDeclaration is null) return new { error = $"'{helper.ToDisplayString()}' is not declared as a C# class." };
        if (helper.ContainingType is not null)
            return new { error = $"'{helper.ToDisplayString()}' is nested; inlining a nested helper is not supported." };
        if (!helperDeclaration.SyntaxTree.Equals(root.SyntaxTree))
            return new { error = $"'{helper.ToDisplayString()}' is declared in another file; move it next to '{ownerTypeName}' first." };

        // ── The helper must be used nowhere but by this field ─────────────────────────────
        foreach (var reference in await SymbolFinder.FindReferencesAsync(helper, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (field.Span.Contains(location.Location.SourceSpan)) continue;

                return new
                {
                    error = $"'{helper.ToDisplayString()}' is also used at " +
                            $"{Path.GetFileName(location.Document.FilePath)}:{location.Location.GetLineSpan().StartLinePosition.Line + 1}; " +
                            "inlining it would leave that use dangling.",
                };
            }
        }

        // ── Every use of the field must be `field.Member` ────────────────────────────────
        var fieldSymbol = model.GetDeclaredSymbol(variable, cancellationToken);
        if (fieldSymbol is null) return new { error = $"'{fieldName}' did not resolve." };

        var accesses = new List<MemberAccessExpressionSyntax>();
        foreach (var reference in await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken))
        {
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
            {
                if (variable.Identifier.Span.Contains(location.Location.SourceSpan)) continue;

                var referenceRoot = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = referenceRoot?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null) continue;

                var access = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>()
                    .FirstOrDefault(candidate => candidate.Expression == node || candidate.Expression.Span.Contains(node.Span));
                if (access is null)
                    return new
                    {
                        error = $"'{fieldName}' is used as a value at " +
                                $"{Path.GetFileName(location.Document.FilePath)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}; " +
                                "only `field.Member` accesses can be inlined.",
                    };

                if (location.Document.Id != document.Id)
                    return new { error = $"'{fieldName}' is accessed from another file; inlining only inside '{ownerTypeName}' is supported." };

                accesses.Add(access);
            }
        }

        // ── Name collisions ──────────────────────────────────────────────────────────────
        var ownerSymbol = model.GetDeclaredSymbol(owner, cancellationToken);
        var helperMembers = helper.GetMembers().Where(member => member is not IMethodSymbol { MethodKind: MethodKind.Constructor }).ToList();
        foreach (var member in helperMembers)
            if (ownerSymbol?.GetMembers(member.Name).Length > 0)
                return new { error = $"'{ownerTypeName}' already has a member named '{member.Name}'; inlining would collide." };

        // ── Build the owner and the file that loses the helper ───────────────────────────
        var helperIndentation = IndentationOf(owner) + "    ";
        var movedMembers = helperDeclaration.Members
            .Where(member => member is not ConstructorDeclarationSyntax)
            .Select(member => member
                .NormalizeWhitespace()
                .WithLeadingTrivia(Whitespace(helperIndentation))
                .WithTrailingTrivia(ElasticCarriageReturnLineFeed))
            .ToList();

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var access in accesses)
            replacements[access] = IdentifierName(access.Name.Identifier.ValueText).WithTriviaFrom(access);

        // The access replacements run FIRST, while their nodes still belong to the tree. The owner and
        // the helper are then found by name: a rebuilt tree does not keep the old nodes as keys.
        SyntaxNode edited = replacements.Count == 0
            ? root
            : root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);

        var editedOwner = edited.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == ownerTypeName);
        if (editedOwner is null) return new { error = "The owner type was lost while the edits were applied." };

        var remaining = editedOwner.Members.Where(member =>
            member is not FieldDeclarationSyntax candidate ||
            !candidate.Declaration.Variables.Any(variable2 => variable2.Identifier.ValueText == fieldName));

        edited = edited.ReplaceNode(editedOwner, editedOwner.WithMembers(List(remaining.Concat(movedMembers))));

        var editedHelper = edited.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == helper.Name);
        if (editedHelper is not null)
            edited = edited.RemoveNode(editedHelper, SyntaxRemoveOptions.KeepLeadingTrivia)!;

        var newText = edited.ToFullString();

        var projected = document.Project.Solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(
            document.Project.Solution, projected, new[] { document.Project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "inlining the class");

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "inline_class",
            owner = ownerSymbol?.ToDisplayString(),
            inlined = helper.ToDisplayString(),
            field = fieldName,
            members = helperMembers.Select(member => member.Name).ToList(),
            file = document.FilePath,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(root.ToFullString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private static string? RefuseHelper(INamedTypeSymbol helper, FieldDeclarationSyntax field)
    {
        if (helper.TypeKind != TypeKind.Class || helper.IsRecord)
            return $"'{helper.ToDisplayString()}' is not a class.";
        if (helper.IsStatic) return $"'{helper.ToDisplayString()}' is static; it has no instance state to inline.";
        if (helper.BaseType is { SpecialType: not SpecialType.System_Object })
            return $"'{helper.ToDisplayString()}' derives from '{helper.BaseType.ToDisplayString()}'; the owner would have to inherit that too.";
        if (helper.AllInterfaces.Length > 0)
            return $"'{helper.ToDisplayString()}' implements interfaces; the owner would have to implement them too.";
        if (helper.GetTypeMembers().Length > 0)
            return $"'{helper.ToDisplayString()}' declares nested types; those are not inlined.";
        if (helper.GetMembers().Any(member => member.IsStatic && member is not IMethodSymbol { MethodKind: MethodKind.Constructor }))
            return $"'{helper.ToDisplayString()}' has static members; only instance members are inlined.";

        var constructors = helper.InstanceConstructors.Where(constructor => !constructor.IsImplicitlyDeclared).ToList();
        foreach (var constructor in constructors)
        {
            if (constructor.Parameters.Length > 0)
                return $"'{helper.ToDisplayString()}' has a constructor with parameters; it cannot be replaced by a field initializer.";

            var declaration = constructor.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault();

            var hasStatements = declaration?.Body?.Statements.Count > 0 ||
                                declaration?.ExpressionBody is not null;
            if (hasStatements)
                return $"'{helper.ToDisplayString()}' has a constructor with a body; it cannot be replaced by a field initializer.";
        }

        return null;
    }

    private static async Task<Document?> FindDocumentAsync(Solution solution, string path)
    {
        foreach (var project in solution.Projects)
            foreach (var candidate in project.Documents)
                if (candidate.FilePath is not null &&
                    string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase))
                    return candidate;

        return null;
    }

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }
}
