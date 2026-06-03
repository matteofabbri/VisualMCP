using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool, Description(
        "When you need every place a symbol is actually used (reads, writes, calls — not just where its name appears as text), use this INSTEAD OF grep. " +
        "Roslyn resolves the real symbol, so it skips same-named-but-unrelated identifiers, comments and string literals that grep falsely hits, and it follows overloads and overrides correctly. " +
        "The working-directory solution auto-loads on first use.")]
    public static async Task<object> FindReferences(
        [Description("Symbol name to search for (class, method, property, field, etc.)")] string symbolName,
        [Description("Optional: restrict to a specific kind — Type, Method, Property, Field, Event (default: all)")] string? kind = null)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

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
