using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Analysis.DataFlow;

/// <summary>One method body, with everything the flow analyses need to read it.</summary>
internal sealed record MethodDeclarationModel(
    int Id, IMethodSymbol Symbol, IMethodSymbol Definition, SyntaxNode Scope, SemanticModel Model, string File,
    bool IsAction);

/// <summary>
/// Reads every method body of a project set once — methods, constructors, accessors and local
/// functions — and keys it on its original definition, so a call site resolves to the body it
/// actually reaches. The two flow analyses (<c>trace_data_flow</c> and <c>find_throw_sites</c>) share
/// this walk instead of each reading the solution again.
/// </summary>
internal static class MethodCollector
{
    public static async Task<List<MethodDeclarationModel>> CollectAsync(
        Solution solution, string? projectName, CancellationToken cancellationToken = default)
    {
        var models = new List<MethodDeclarationModel>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var node in root.DescendantNodes())
                {
                    var scope = node switch
                    {
                        BaseMethodDeclarationSyntax method when method.Body is not null || method.ExpressionBody is not null
                            => (SyntaxNode)method,
                        LocalFunctionStatementSyntax local when local.Body is not null || local.ExpressionBody is not null
                            => local,
                        AccessorDeclarationSyntax accessor when accessor.Body is not null || accessor.ExpressionBody is not null
                            => accessor,
                        _ => null,
                    };
                    if (scope is null) continue;

                    if (model.GetDeclaredSymbol(node, cancellationToken) is not IMethodSymbol symbol) continue;

                    var definition = (symbol.ReducedFrom ?? symbol).OriginalDefinition;
                    if (!seen.Add(definition)) continue;

                    models.Add(new MethodDeclarationModel(
                        models.Count, symbol, definition, scope, model,
                        document.FilePath ?? document.Name, IsAction(symbol)));
                }
            }
        }

        return models;
    }

    /// <summary>A controller action: the type derives from <c>ControllerBase</c> or carries <c>[ApiController]</c>.</summary>
    public static bool IsAction(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic) return false;

        var type = method.ContainingType;
        if (type is null) return false;

        for (var current = type; current is not null; current = current.BaseType)
            if (current.Name == "ControllerBase")
                return true;

        return type.GetAttributes().Any(attribute =>
            (attribute.AttributeClass?.Name ?? string.Empty) is "ApiControllerAttribute" or "ApiController");
    }

    public static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static string FileOf(SyntaxNode node, SemanticModel model) =>
        node.SyntaxTree.FilePath ?? model.Compilation.AssemblyName ?? "(text)";
}
