using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Navigation;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool, Description(
        "When you need to locate a class, interface, method, record, enum or struct by name, use this INSTEAD OF grep. " +
        "It matches real declared symbols via Roslyn's semantic model, ignoring comments, strings and unrelated text that grep falsely hits, " +
        "and returns each match's kind, containing type/namespace, file and line. Supports exact or partial-name matching. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindSymbol(
        [Description("Symbol name to search for (supports partial/contains match)")] string symbolName,
        [Description("If true, matches any symbol whose name contains symbolName; if false (default), matches the exact name")] bool partialMatch = false)
        => NavigationImpl.FindSymbolAsync(symbolName, partialMatch);
}
