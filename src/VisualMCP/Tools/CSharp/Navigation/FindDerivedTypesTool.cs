using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class FindDerivedTypesTool
{
    [McpServerTool, Description(
        "When you need the subclasses of a class or the types extending an interface (the downward inheritance tree), use this INSTEAD OF grep. " +
        "It walks the real type hierarchy via Roslyn, optionally transitively — relationships that are invisible to text search. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindDerivedTypes(
        [Description("Full or partial name of the base class or interface")] string typeName,
        [Description("Include transitive descendants, not just direct children (default: true)")] bool transitive = true)
        => NavigationImpl.FindDerivedTypesAsync(typeName, transitive);
}
