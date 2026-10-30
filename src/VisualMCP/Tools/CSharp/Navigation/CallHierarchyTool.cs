using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class CallHierarchyTool
{
    [McpServerTool(Name = "call_hierarchy"), Description(
        "The call hierarchy in BOTH directions, as a tree: incoming = who calls this member (each caller " +
        "resolves to the member that contains the use), outgoing = what this method calls (each invocation " +
        "resolved semantically). depth counts levels from the symbol (default 2, max 5); a shared node budget " +
        "stops a wide hierarchy from walking the whole solution. find_callers answers one level; this answers " +
        "the chain. Read-only.")]
    public static Task<object> CallHierarchy(
        ICallHierarchyService hierarchy,
        [Description("Method, property or type name.")] string symbolName,
        [Description("Containing type, when the name is ambiguous.")] string? containingType = null,
        [Description("incoming | outgoing | both (default)")] string direction = "both",
        [Description("Levels to walk from the symbol (default 2, max 5).")] int depth = 2)
        => hierarchy.BuildAsync(symbolName, containingType, direction, depth);
}
