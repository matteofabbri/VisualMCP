using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.Documentation;

namespace VisualMCP.Tools.Documentation;

[McpServerToolType]
public static class FindUndocumentedPublicApiTool
{
    [McpServerTool, Description(
        "When you need to find public/protected types and members that lack XML doc comments (e.g. before publishing a library), use this INSTEAD OF scanning files manually. " +
        "Roslyn enumerates the real public surface across the whole solution, so nothing is missed and internal members are correctly excluded. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> FindUndocumentedPublicApi(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Include protected members in addition to public (default: true)")] bool includeProtected = true)
        => DocumentationImpl.FindUndocumentedAsync(projectName, includeProtected);
}
