using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Change nullability", with a compiler gate: it toggles the annotation on a declaration and
/// refuses when the change would introduce new nullability diagnostics, instead of propagating blindly.
/// </summary>
public interface IChangeNullabilityService
{
    Task<object> ChangeNullability(
        string filePath,
        int line,
        string? direction = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ChangeNullabilityService : IChangeNullabilityService
{
    private readonly IRoslynWorkspace _workspace;

    public ChangeNullabilityService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ChangeNullability(
        string filePath,
        int line,
        string? direction = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line < 1) return new { error = "Line must be 1-based." };

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
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is null || model is null || tree is null || compilation is null)
            return new { error = $"Could not read the syntax or semantic model of {Path.GetFileName(path)}." };

        var target = FindTypeOnLine(root, line);
        if (target is null)
            return new { error = $"No field, property, local, parameter or return type starts on line {line}." };

        var typeInfo = model.GetTypeInfo(target, cancellationToken).Type;
        if (typeInfo is null || typeInfo.TypeKind == TypeKind.Error)
            return new { error = "The declaration's type did not resolve." };

        var isNullableAnnotation = target is NullableTypeSyntax;
        bool makeNullable;
        if (direction is null)
        {
            makeNullable = !isNullableAnnotation;
        }
        else
        {
            makeNullable = direction.Equals("nullable", StringComparison.OrdinalIgnoreCase);
            if (!makeNullable && !isNullableAnnotation)
                return new { error = "The type is already non-nullable." };
            if (makeNullable && isNullableAnnotation)
                return new { error = "The type is already nullable." };
        }

        if (makeNullable && typeInfo.IsValueType)
            return new { error = $"'{typeInfo.ToDisplayString()}' is a value type: '?' would mean Nullable<T>, not a nullability annotation." };

        var newType = makeNullable
            ? NullableType(target.WithoutTrivia())
            : NullableTypeSyntaxUnwrapped(target).WithoutTrivia();

        newType = newType.WithTriviaFrom(target);
        var newRoot = root.ReplaceNode(target, newType);

        // ── Gate: the change must not introduce nullability diagnostics ──────────────────
        var before = CountNullability(compilation);
        var changedTree = CSharpSyntaxTree.Create(
            (CSharpSyntaxNode)newRoot, (CSharpParseOptions)tree.Options, document.FilePath, encoding: null);
        var after = CountNullability(compilation.ReplaceSyntaxTree(tree, changedTree));
        var delta = after - before;

        if (delta > 0)
            return new
            {
                error = $"Refused: this annotation would introduce {delta} new nullability diagnostic(s) " +
                        $"({before} → {after} in the project). Fix the affected usages first.",
                before,
                after,
            };

        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "change_nullability",
            type = typeInfo.ToDisplayString(),
            changedTo = newType.ToString(),
            file = document.FilePath,
            line,
            dryRun,
            applied = !dryRun,
            nullabilityDiagnostics = new { before, after, delta },
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>The type syntax of the first field/property/local/parameter/return declaration starting on that line.</summary>
    private static TypeSyntax? FindTypeOnLine(SyntaxNode root, int line)
    {
        var candidates = root.DescendantNodes()
            .Where(node => node is VariableDeclarationSyntax or PropertyDeclarationSyntax or ParameterSyntax
                             or MethodDeclarationSyntax or PropertyDeclarationSyntax)
            .Where(node => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line)
            .OrderBy(node => node.SpanStart)
            .Select(node => node switch
            {
                VariableDeclarationSyntax variable => variable.Type,
                PropertyDeclarationSyntax property => property.Type,
                ParameterSyntax parameter => parameter.Type,
                MethodDeclarationSyntax method => IsVoid(method.ReturnType) ? null : method.ReturnType,
                _ => null,
            })
            .Where(type => type is not null)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static bool IsVoid(TypeSyntax type) =>
        type is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    private static TypeSyntax NullableTypeSyntaxUnwrapped(TypeSyntax type) =>
        type is NullableTypeSyntax nullable ? nullable.ElementType.WithTriviaFrom(type) : type;

    private static int CountNullability(Compilation compilation) =>
        compilation.GetDiagnostics()
            .Count(diagnostic => diagnostic.Id.StartsWith("CS86", StringComparison.Ordinal) ||
                                 diagnostic.Id.StartsWith("CS87", StringComparison.Ordinal));
}
