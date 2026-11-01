using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>Rider's "Add params modifier": the last array parameter starts collecting call sites.</summary>
public interface IAddParamsModifierService
{
    Task<object> AddParams(
        string filePath,
        string methodName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed partial class AddParamsModifierService : IAddParamsModifierService
{
    private readonly IRoslynWorkspace _workspace;

    public AddParamsModifierService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AddParams(
        string filePath,
        string methodName,
        string? containingType = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "A method name is required." };

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

        if (document.Project.Language == LanguageNames.VisualBasic)
            return await TryVisualBasicAsync(_workspace, document, methodName, containingType, dryRun, cancellationToken)
                   ?? new { error = "The Visual Basic rewrite did not produce a result." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var candidates = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .Where(method => containingType is null ||
                             string.Equals(model.GetDeclaredSymbol(method, cancellationToken)?.ContainingType?.Name,
                                           containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return new { error = $"No method '{methodName}' in {Path.GetFileName(path)}." };
        if (candidates.Count > 1) return new { error = $"Several methods named '{methodName}'; address one overload at a time." };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method)
            return new { error = $"'{methodName}' did not resolve to a method." };

        if (method.IsOverride) return new { error = $"'{methodName}' overrides a base member: adding params would break the override." };
        if (method.ExplicitInterfaceImplementations.Length > 0 ||
            (method.ContainingType is { } owner && owner.AllInterfaces
                .SelectMany(reference => reference.GetMembers(method.Name))
                .Any(interfaceMember => SymbolEqualityComparer.Default.Equals(
                    owner.FindImplementationForInterfaceMember(interfaceMember), method))))
        {
            return new { error = $"'{methodName}' implements an interface member: the signature must stay identical." };
        }

        var parameters = declaration.ParameterList.Parameters;
        var last = parameters.LastOrDefault();
        if (last is null) return new { error = $"'{methodName}' has no parameters." };
        if (last.Modifiers.Any(SyntaxKind.ParamsKeyword)) return new { error = "The last parameter is already 'params'." };
        if (last.Modifiers.Any(SyntaxKind.RefKeyword) || last.Modifiers.Any(SyntaxKind.OutKeyword) || last.Modifiers.Any(SyntaxKind.InKeyword))
            return new { error = "A ref/out/in parameter cannot be params." };
        if (last.Type is not ArrayTypeSyntax array) return new { error = "The last parameter is not an array; params needs T[]." };
        if (array.RankSpecifiers.Any(rank => rank.Rank > 1)) return new { error = "A multi-dimensional array cannot be params." };

        var updated = last.WithModifiers(last.Modifiers.Add(Token(SyntaxKind.ParamsKeyword).WithTrailingTrivia(Space)));
        var newDeclaration = declaration.WithParameterList(
            declaration.ParameterList.WithParameters(parameters.Replace(last, updated)));

        var newRoot = root.ReplaceNode(declaration, newDeclaration);
        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var newText = newRoot.ToFullString();

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);

        return new
        {
            @refactoring = "add_params",
            method = method.ToDisplayString(),
            parameter = $"{array.ElementType}[] {last.Identifier.ValueText}",
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(oldText, newText),
            note = dryRun
                ? "Preview only: pass dryRun=false to write the file."
                : "File written. Existing call sites keep compiling: they pass an array explicitly.",
        };
    }
}
