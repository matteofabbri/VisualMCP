using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool, Description(
        "When you need to locate a class, interface, method, record, enum or struct by name, use this INSTEAD OF grep. " +
        "It matches real declared symbols via Roslyn's semantic model, ignoring comments, strings and unrelated text that grep falsely hits, " +
        "and returns each match's kind, containing type/namespace, file and line. Supports exact or partial-name matching. " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> FindSymbol(
        [Description("Symbol name to search for (supports partial/contains match)")] string symbolName,
        [Description("If true, matches any symbol whose name contains symbolName; if false (default), matches the exact name")] bool partialMatch = false)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

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
