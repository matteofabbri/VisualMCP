using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool, Description("Find every location in the solution where a named symbol is referenced (not just declared). Requires LoadSolution first.")]
    public static async Task<object> FindReferences(
        [Description("Symbol name to search for (class, method, property, field, etc.)")] string symbolName,
        [Description("Optional: restrict to a specific kind — Type, Method, Property, Field, Event (default: all)")] string? kind = null)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.All);

        if (kind is not null)
            declarations = declarations.Where(s => s.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase));

        var symbols = declarations.ToList();
        if (symbols.Count == 0)
            return new { error = $"No symbol named '{symbolName}' found." };

        var results = new List<object>();
        foreach (var symbol in symbols)
        {
            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
            foreach (var refGroup in refs)
            {
                var locations = refGroup.Locations.Select(l => new
                {
                    FilePath = l.Document.FilePath,
                    Line     = l.Location.GetLineSpan().StartLinePosition.Line + 1,
                    Column   = l.Location.GetLineSpan().StartLinePosition.Character + 1,
                }).OrderBy(l => l.FilePath).ThenBy(l => l.Line).ToList();

                results.Add(new
                {
                    Symbol     = symbol.ToDisplayString(),
                    Kind       = symbol.Kind.ToString(),
                    RefCount   = locations.Count,
                    References = locations,
                });
            }
        }

        return new { symbolName, symbolsFound = symbols.Count, results };
    }
}
