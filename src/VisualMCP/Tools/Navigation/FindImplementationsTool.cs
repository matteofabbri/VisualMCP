using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Navigation;

namespace VisualMCP.Tools.Navigation;

[McpServerToolType]
public static class FindImplementationsTool
{
    [McpServerTool, Description(
        "When you need the concrete types that implement an interface (and the members implementing each interface member), use this INSTEAD OF grepping for the interface name. " +
        "Roslyn resolves real implementations, including implicit ones the text never names, which grep misses. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindImplementations(
        [Description("Full or partial name of the interface (e.g. 'IMyService' or 'MyNamespace.IMyService')")] string interfaceName)
        => NavigationImpl.FindImplementationsAsync(interfaceName);
}
