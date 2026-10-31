using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Navigation;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool, Description(
        "When you need to locate a class, interface, method, record, enum or struct by name, use this INSTEAD OF grep. " +
        "It matches real declared symbols via Roslyn's semantic model, ignoring comments, strings and unrelated text that grep falsely hits, " +
        "and returns each match's kind, containing type/namespace, file and line. Ranks exact > prefix > CamelHumps > contains; " +
        "partialMatch adds contains matches, and when nothing matches a fuzzy subsequence fallback ('custord' finds " +
        "CustomerOrder) names the closest declarations instead of returning nothing. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindSymbol(
        INavigationService nav,
        [Description("Symbol name to search for (supports partial/contains match)")] string symbolName,
        [Description("If true, matches any symbol whose name contains symbolName; if false (default), matches the exact name")] bool partialMatch = false,
        [Description("When nothing matched, fall back to a subsequence match ('custord' finds CustomerOrder). Default true.")] bool fuzzy = true)
        => nav.FindSymbolAsync(symbolName, partialMatch, fuzzy);
}
