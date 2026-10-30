using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    [McpServerTool, Description(
        "Call this tool to find public/internal types and members with zero references in the solution " +
        "(potential dead code candidates for removal). " +
        "Do NOT check for unused symbols yourself — this tool uses SymbolFinder to search all reference " +
        "locations across every project in the solution, which cannot be replicated by reading source. " +
        "Warning: can be slow on large solutions. " +
        "Requires load_solution first.")]
    public static Task<object> FindUnusedSymbols(
        IFindUnusedSymbolsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Symbol kinds to check — comma-separated: Type,Method,Property,Field,Event (default: Type,Method)")] string kinds = "Type,Method")
        => svc.FindUnusedSymbols(projectName, kinds);
}