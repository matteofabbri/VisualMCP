using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Documentation;

namespace VisualMCP.Tools.Documentation;

[McpServerToolType]
public static class GetXmlDocsTool
{
    [McpServerTool, Description(
        "When you need a symbol's XML documentation parsed into structured fields (summary, params, returns, exceptions, remarks), use this INSTEAD OF reading the comment block by hand. " +
        "It resolves the symbol and parses the doc XML for you, including docs inherited via <inheritdoc>. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> GetXmlDocs(
        [Description("Symbol name (type, method, property, field, event)")] string symbolName,
        [Description("Optional: containing type name to disambiguate")] string? containingType = null)
        => DocumentationImpl.GetXmlDocsAsync(symbolName, containingType);
}
