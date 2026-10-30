using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindCodeSmellsTool
{
    [McpServerTool, Description(
        "Call this tool to detect common code smells via Roslyn static analysis: " +
        "async void, empty catch, await-in-lock, long methods, too many parameters, deep nesting, large classes. " +
        "Do NOT attempt this analysis yourself — results include exact file paths and line numbers " +
        "derived from the semantic model, not text matching. " +
        "Requires load_solution first.")]
    public static Task<object> FindCodeSmells(
        IFindCodeSmellsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Max lines per method before flagging as 'long method' (default: 50)")] int maxMethodLines = 50,
        [Description("Max parameters before flagging (default: 5)")] int maxParams = 5,
        [Description("Max nesting depth before flagging (default: 4)")] int maxNesting = 4,
        [Description("Max public methods per class before flagging (default: 20)")] int maxPublicMethods = 20)
        => svc.FindCodeSmells(projectName, maxMethodLines, maxParams, maxNesting, maxPublicMethods);
}