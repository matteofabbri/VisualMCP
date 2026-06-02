using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VsSolutionServer.Workspace;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool, Description("Search for a named symbol (class, interface, method, record, enum, struct) using Roslyn's semantic model across the loaded solution. Requires LoadSolution to have been called first.")]
    public static async Task<object> FindSymbol(
        [Description("Symbol name to search for (supports partial/contains match)")] string symbolName,
        [Description("If true, matches any symbol whose name contains symbolName; if false (default), matches the exact name")] bool partialMatch = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        IEnumerable<ISymbol> symbols;

        if (partialMatch)
        {
            symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                solution,
                name => name.Contains(symbolName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.TypeAndMember);
        }
        else
        {
            symbols = await SymbolFinder.FindSourceDeclarationsAsync(
                solution,
                symbolName,
                ignoreCase: true,
                filter: SymbolFilter.TypeAndMember);
        }

        var matches = symbols.Select(s =>
        {
            var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
            var filePath = loc?.SourceTree?.FilePath;
            var line = loc?.GetLineSpan().StartLinePosition.Line + 1;
            return new
            {
                Name = s.Name,
                Kind = s.Kind.ToString(),
                ContainingType = s.ContainingType?.ToDisplayString(),
                ContainingNamespace = s.ContainingNamespace?.ToDisplayString(),
                FullyQualifiedName = s.ToDisplayString(),
                FilePath = filePath,
                Line = line,
            };
        }).ToList();

        return new
        {
            symbolName,
            partialMatch,
            matchCount = matches.Count,
            matches
        };
    }
}
