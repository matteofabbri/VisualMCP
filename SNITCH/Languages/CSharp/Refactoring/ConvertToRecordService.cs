using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Convert to record". The plain form flips the declaration to <c>record</c> (a deliberate
/// change of equality/ToString semantics). The positional form additionally accepts only the shape
/// that can be expressed as a primary constructor: one constructor whose body assigns each parameter
/// to an auto-property of the same name.
/// </summary>
public interface IConvertToRecordService
{
    Task<object> ConvertToRecord(
        string filePath,
        string typeName,
        bool positional = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ConvertToRecordService : IConvertToRecordService
{
    private readonly IRoslynWorkspace _workspace;

    public ConvertToRecordService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConvertToRecord(
        string filePath,
        string typeName,
        bool positional = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type to convert is required." };

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

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (declaration is null)
            return new { error = $"No class '{typeName}' in {Path.GetFileName(path)}." };

        if (declaration.Members.OfType<DestructorDeclarationSyntax>().Any())
            return new { error = $"'{typeName}' has a destructor; a record cannot." };

        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };
        if (typeSymbol.BaseType is { SpecialType: not SpecialType.System_Object })
            return new { error = $"'{typeName}' derives from '{typeSymbol.BaseType.ToDisplayString()}'; a record can only derive from another record." };

        var keyword = Token(SyntaxKind.RecordKeyword).WithTriviaFrom(declaration.Keyword);

        if (!positional)
        {
            var record = RecordDeclaration(SyntaxKind.RecordDeclaration, keyword, declaration.Identifier)
                .WithAttributeLists(declaration.AttributeLists)
                .WithModifiers(declaration.Modifiers)
                .WithTypeParameterList(declaration.TypeParameterList)
                .WithBaseList(declaration.BaseList)
                .WithConstraintClauses(declaration.ConstraintClauses)
                .WithOpenBraceToken(declaration.OpenBraceToken)
                .WithMembers(declaration.Members)
                .WithCloseBraceToken(declaration.CloseBraceToken)
                .WithLeadingTrivia(declaration.GetLeadingTrivia())
                .WithTrailingTrivia(declaration.GetTrailingTrivia());

            return await ApplyAsync(document, root, declaration, record,
                "the conversion", "record", positional: false, dryRun, cancellationToken);
        }

        // ── Positional: one ctor that assigns every parameter to an auto-property ─────────
        var constructors = declaration.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (constructors.Count != 1)
            return new { error = $"'{typeName}' has {constructors.Count} constructors; a positional record needs exactly one." };

        var constructor = constructors[0];
        if (constructor.Initializer is not null)
            return new { error = $"'{typeName}' initialises a base type in its constructor; that cannot be expressed positionally." };
        if (constructor.Body is null)
            return new { error = $"'{typeName}' has an expression-bodied constructor; a positional record needs assignments in a body." };

        var assignments = new List<(string Member, string Parameter)>();
        foreach (var statement in constructor.Body.Statements)
        {
            if (statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment } ||
                !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                return new { error = "The constructor must contain only `member = parameter;` assignments." };

            var member = assignment.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
                _ => null,
            };

            if (member is null || assignment.Right is not IdentifierNameSyntax parameter)
                return new { error = "The constructor must contain only `member = parameter;` assignments." };

            var property = declaration.Members.OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault(candidate => candidate.Identifier.ValueText == member);
            if (property is null)
                return new
                {
                    error = $"'{member}' is not a property of '{typeName}'. A positional record replaces auto-properties, " +
                            "not fields or methods; use positional=false to just change the keyword.",
                };

            var accessors = property.AccessorList?.Accessors;
            var isAuto = accessors is not null &&
                         accessors.Value.All(accessor => accessor.Body is null && accessor.ExpressionBody is null) &&
                         accessors.Value.All(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ||
                                                          accessor.IsKind(SyntaxKind.InitAccessorDeclaration));
            if (!isAuto)
                return new { error = $"'{member}' has an accessor with a body; a positional record only replaces auto-properties." };

            assignments.Add((member, parameter.Identifier.ValueText));
        }

        var assignedMembers = assignments.Select(assignment => assignment.Member).ToHashSet(StringComparer.Ordinal);
        var propertyNames = declaration.Members.OfType<PropertyDeclarationSyntax>()
            .Where(property => assignedMembers.Contains(property.Identifier.ValueText))
            .Select(property => property.Identifier.ValueText)
            .ToList();

        if (!assignedMembers.SetEquals(propertyNames))
            return new { error = "Each assigned member must be a property declared in this type." };

        // The positional parameter keeps the property name so every reference to it still resolves.
        var parameters = new List<string>();
        foreach (var (member, parameterName) in assignments)
        {
            var parameter = constructor.ParameterList.Parameters
                .FirstOrDefault(candidate => candidate.Identifier.ValueText == parameterName);
            if (parameter?.Type is null) return new { error = $"'{parameterName}' is not a typed parameter of the constructor." };

            parameters.Add($"{parameter.Type.WithoutTrivia()} {member}");
        }

        var remaining = declaration.Members
            .Where(member => member != constructor &&
                             (member is not PropertyDeclarationSyntax property || !assignedMembers.Contains(property.Identifier.ValueText)))
            .ToList();

        // Built from text: the record syntax factory leaves the braces "missing" depending on the
        // overload, and a hand-built header is easier to get right than a dozen With* calls.
        var modifiers = string.Join(" ", declaration.Modifiers.Select(modifier => modifier.Text));
        var header =
            (modifiers.Length > 0 ? modifiers + " " : string.Empty) +
            $"record {declaration.Identifier.Text}" +
            (declaration.TypeParameterList?.ToString() ?? string.Empty) +
            $"({string.Join(", ", parameters)})" +
            (declaration.BaseList is null ? string.Empty : " " + declaration.BaseList) +
            string.Concat(declaration.ConstraintClauses.Select(clause => " " + clause));

        var body = "{" + Environment.NewLine +
                   string.Join(Environment.NewLine,
                       remaining.Select(member => "    " + member.NormalizeWhitespace().ToFullString()
                           .Replace("\n", "\n    ").TrimEnd())) +
                   Environment.NewLine + "}";

        if (ParseMemberDeclaration(header + Environment.NewLine + body) is not RecordDeclarationSyntax positionalRecord)
            return new { error = "Could not build the positional record declaration." };

        positionalRecord = positionalRecord
            .WithLeadingTrivia(declaration.GetLeadingTrivia())
            .WithTrailingTrivia(declaration.GetTrailingTrivia());

        return await ApplyAsync(document, root, declaration, positionalRecord,
            "the conversion", "record", positional: true, dryRun, cancellationToken);
    }

    private async Task<object> ApplyAsync(
        Document document,
        SyntaxNode root,
        ClassDeclarationSyntax declaration,
        RecordDeclarationSyntax record,
        string what,
        string result,
        bool positional,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var newText = root.ReplaceNode(declaration, record).ToFullString();

        var before = await document.Project.GetCompilationAsync(cancellationToken);
        var projected = document.Project.Solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(
            document.Project.Solution, projected, new[] { document.Project.Id }, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, what);

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "convert_to_record",
            type = declaration.Identifier.ValueText,
            positional,
            file = document.FilePath,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(root.ToFullString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }
}
