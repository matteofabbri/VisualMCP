using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindDeadCodeTool
{
    [McpServerTool, Description(
        "Call this tool to find unreachable code in the codebase using Roslyn's control-flow analysis: " +
        "statements after unconditional return/throw/break/continue, always-false conditions, " +
        "switch arms that can never match, and catch blocks for exceptions that cannot be thrown. " +
        "Do NOT identify dead code by reading source — reliable detection requires building and " +
        "analysing the full control-flow graph, which only Roslyn can do accurately. " +
        "Requires load_solution first.")]
    public static Task<object> FindDeadCode(
        IFindDeadCodeService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null)
        => svc.FindDeadCode(projectName);
}