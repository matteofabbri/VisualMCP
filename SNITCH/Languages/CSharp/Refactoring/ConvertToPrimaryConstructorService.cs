using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Convert to primary constructor", for the case that is a pure syntax move: one
/// constructor whose body only assigns parameters to fields, each declared without an initializer.
/// The parameters move to the type header and the assignments become field initializers.
/// </summary>
public interface IConvertToPrimaryConstructorService
{
    Task<object> ConvertToPrimaryConstructor(
        string filePath,
        string typeName,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ConvertToPrimaryConstructorService : IConvertToPrimaryConstructorService
{
    private readonly IRoslynWorkspace _workspace;

    public ConvertToPrimaryConstructorService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConvertToPrimaryConstructor(
        string filePath,
        string typeName,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type name is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || tree is null || compilation is null)
            return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var declaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (declaration is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(path)}." };
        if (declaration.ParameterList is not null)
            return new { error = $"'{typeName}' already has a primary constructor." };

        var constructors = declaration.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (constructors.Count == 0) return new { error = $"'{typeName}' has no explicit constructor." };
        if (constructors.Count > 1) return new { error = $"'{typeName}' has {constructors.Count} constructors; keep one." };

        var constructor = constructors[0];
        if (constructor.Body is null) return new { error = "The constructor has no body." };

        // Every statement must be `field = parameter;` (this.field accepted).
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var statement in constructor.Body.Statements)
        {
            if (statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment } ||
                assignment.Right is not IdentifierNameSyntax parameterName)
            {
                return new { error = "The constructor body must only assign parameters to fields; anything else changes behaviour." };
            }

            var fieldName = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
                _ => null,
            };

            if (fieldName is null || !constructor.ParameterList.Parameters.Any(p => p.Identifier.ValueText == parameterName.Identifier.ValueText))
                return new { error = "The constructor body must only assign parameters to fields." };

            if (!assignments.TryAdd(fieldName, parameterName.Identifier.ValueText))
                return new { error = $"Field '{fieldName}' is assigned twice in the constructor." };
        }

        if (assignments.Count == 0)
            return new { error = "The constructor assigns nothing; a primary constructor would not replace anything." };

        // Each assigned field must be declared here, once, without an initializer.
        var updatedMembers = new List<MemberDeclarationSyntax>();
        var converted = 0;
        foreach (var member in declaration.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                var names = field.Declaration.Variables.Select(variable => variable.Identifier.ValueText).ToList();
                var assigned = names.Where(assignments.ContainsKey).ToList();
                if (assigned.Count == 0) { updatedMembers.Add(field); continue; }
                if (names.Count > 1) return new { error = $"'{field}' declares several fields; split the declaration first." };
                if (field.Declaration.Variables[0].Initializer is not null)
                    return new { error = $"Field '{names[0]}' has an initializer and is assigned in the constructor; keep one." };

                updatedMembers.Add(field.WithDeclaration(field.Declaration.WithVariables(
                    SingletonSeparatedList(field.Declaration.Variables[0]
                        .WithInitializer(EqualsValueClause(
                            Token(SyntaxKind.EqualsToken).WithLeadingTrivia(Space).WithTrailingTrivia(Space),
                            IdentifierName(assignments[names[0]])))))));
                converted++;
                continue;
            }

            if (member == constructor) continue; // it becomes the parameter list
            updatedMembers.Add(member);
        }

        if (converted != assignments.Count)
            return new { error = "Not every assigned field could be rewritten; nothing was changed." };

        // The newline before the class body belongs to the identifier's trailing trivia; move it onto
        // the parameter list, or the header renders as `class C` + newline + `(int value)`.
        var bodyLeading = declaration.Identifier.TrailingTrivia;

        var newDeclaration = declaration
            .WithIdentifier(declaration.Identifier.WithTrailingTrivia(default(SyntaxTriviaList)))
            .WithParameterList(constructor.ParameterList.WithTrailingTrivia(bodyLeading))
            .WithMembers(List(updatedMembers));
        newDeclaration = newDeclaration
            .WithLeadingTrivia(declaration.GetLeadingTrivia())
            .WithTrailingTrivia(declaration.GetTrailingTrivia());

        var newRoot = root.ReplaceNode(declaration, newDeclaration);

        // ── Gate: the project must still compile ──────────────────────────────────────────
        var before = compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var changedTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)newRoot, (CSharpParseOptions)tree.Options,
            document.FilePath, encoding: null);
        var afterDiagnostics = compilation.ReplaceSyntaxTree(tree, changedTree)
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = afterDiagnostics.Count - before;
        if (added > 0)
            return new
            {
                error = $"Refused: the primary constructor would add {added} compiler error(s) ({before} → {afterDiagnostics.Count}).",
                errors = afterDiagnostics.Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                    .Distinct().Take(8).ToList(),
            };

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "convert_to_primary_constructor",
            type = typeName,
            parameters = constructor.ParameterList.Parameters.Count,
            fieldsInitialised = converted,
            compilerErrors = new { before, after = afterDiagnostics.Count },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }
}
