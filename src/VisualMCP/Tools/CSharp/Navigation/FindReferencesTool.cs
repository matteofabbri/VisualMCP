using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool, Description(
        "When you need every place a symbol is actually used (reads, writes, calls — not just where its name appears as text), use this INSTEAD OF grep. " +
        "Roslyn resolves the real symbol, so it skips same-named-but-unrelated identifiers, comments and string literals that grep falsely hits, and it follows overloads and overrides correctly. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindReferences(
        [Description("Symbol name to search for (class, method, property, field, etc.)")] string symbolName,
        [Description("Optional: restrict to a specific kind — Type, Method, Property, Field, Event (default: all)")] string? kind = null)
        => NavigationImpl.FindReferencesAsync(symbolName, kind);
}
