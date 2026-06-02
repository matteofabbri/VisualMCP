using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class FindCallersTool
{
    [McpServerTool, Description("Find all methods/properties that call a given method or access a given property — the Call Hierarchy view from Visual Studio. Requires LoadSolution first.")]
    public static async Task<object> FindCallers(
        [Description("Name of the method, property, or constructor to analyse")] string symbolName,
        [Description("Optional: containing type name to disambiguate overloads")] string? containingType = null)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(symbolName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Member);

        var symbols = candidates
            .Where(s => containingType is null ||
                        (s.ContainingType?.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (symbols.Count == 0)
            return new { error = $"No member named '{symbolName}' found." };

        var results = new List<object>();
        foreach (var symbol in symbols)
        {
            var callers = await SymbolFinder.FindCallersAsync(symbol, solution);

            var callerList = callers
                .Where(c => c.IsDirect)
                .Select(c =>
                {
                    var callerLoc = c.CallingSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                    var callSites = c.Locations.Select(l => new
                    {
                        FilePath = l.SourceTree?.FilePath,
                        Line     = l.GetLineSpan().StartLinePosition.Line + 1,
                    }).ToList();

                    return new
                    {
                        Caller     = c.CallingSymbol.ToDisplayString(),
                        CallerKind = c.CallingSymbol.Kind.ToString(),
                        FilePath   = callerLoc?.SourceTree?.FilePath,
                        Line       = callerLoc?.GetLineSpan().StartLinePosition.Line + 1,
                        CallSites  = callSites,
                    };
                })
                .OrderBy(c => c.Caller)
                .ToList();

            results.Add(new
            {
                Symbol      = symbol.ToDisplayString(),
                Kind        = symbol.Kind.ToString(),
                CallerCount = callerList.Count,
                Callers     = callerList,
            });
        }

        return new { symbolName, matchCount = results.Count, results };
    }
}
