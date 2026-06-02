using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VsSolutionServer.Workspace;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class FindDerivedTypesTool
{
    [McpServerTool, Description("Find all types that derive from a class or extend an interface (the downward inheritance tree). Requires LoadSolution first.")]
    public static async Task<object> FindDerivedTypes(
        [Description("Full or partial name of the base class or interface")] string typeName,
        [Description("Include transitive descendants, not just direct children (default: true)")] bool transitive = true)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var types = candidates.OfType<INamedTypeSymbol>().ToList();
        if (types.Count == 0)
            return new { error = $"Type '{typeName}' not found in the loaded solution." };

        var results = new List<object>();
        foreach (var type in types)
        {
            IEnumerable<INamedTypeSymbol> derived;
            if (type.TypeKind == TypeKind.Interface)
                derived = await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive);
            else
                derived = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive);

            var derivedList = derived.Select(d =>
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                return new
                {
                    Name          = d.ToDisplayString(),
                    Kind          = d.TypeKind.ToString(),
                    Accessibility = d.DeclaredAccessibility.ToString(),
                    IsAbstract    = d.IsAbstract,
                    IsSealed      = d.IsSealed,
                    FilePath      = loc?.SourceTree?.FilePath,
                    Line          = loc?.GetLineSpan().StartLinePosition.Line + 1,
                };
            }).OrderBy(d => d.Name).ToList();

            results.Add(new
            {
                BaseType       = type.ToDisplayString(),
                Kind           = type.TypeKind.ToString(),
                DerivedCount   = derivedList.Count,
                Transitive     = transitive,
                DerivedTypes   = derivedList,
            });
        }

        return new { typeName, matchCount = results.Count, results };
    }
}
