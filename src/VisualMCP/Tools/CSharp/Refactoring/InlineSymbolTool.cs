using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class InlineSymbolTool
{
    [McpServerTool, Description(
        "Inline a symbol at all its use sites and remove the original declaration. " +
        "Supports: (1) local variables with a single initializer, " +
        "(2) methods/properties with a single expression body or a single 'return' statement. " +
        "Refuses if the body is multi-statement or if the symbol has no references. " +
        "Equivalent to ReSharper 'Inline Variable/Method'. Requires LoadSolution first.")]
    public static Task<object> InlineSymbol(
        IInlineSymbolService svc,
        [Description("Symbol name to inline (local variable, method, or property)")] string symbolName,
        [Description("Optional: containing type name to disambiguate methods/properties")] string? containingType = null,
        [Description("Dry run — show what would change without writing to disk (default: false)")] bool dryRun = false)
        => svc.InlineSymbol(symbolName, containingType, dryRun);
}