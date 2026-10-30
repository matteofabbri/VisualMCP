using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Navigation;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class FindCallersTool
{
    [McpServerTool, Description(
        "When you need to know who calls a method/constructor or accesses a property (the Visual Studio Call Hierarchy), use this INSTEAD OF grep. " +
        "It resolves callers semantically through overloads and interface dispatch, which plain text search cannot do. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindCallers(
        INavigationService nav,
        [Description("Name of the method, property, or constructor to analyse")] string symbolName,
        [Description("Optional: containing type name to disambiguate overloads")] string? containingType = null)
        => nav.FindCallersAsync(symbolName, containingType);
}