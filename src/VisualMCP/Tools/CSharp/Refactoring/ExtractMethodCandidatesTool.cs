using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ExtractMethodCandidatesTool
{
    [McpServerTool, Description(
        "When you want to break up long methods, use this INSTEAD OF eyeballing the source: it analyses data flow via Roslyn to find cohesive statement blocks " +
        "(shared locals, single responsibility) that are good Extract Method candidates, and reports their location and span. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> ExtractMethodCandidates(
        IExtractMethodCandidatesService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Minimum method length in lines to analyse (default: 30)")] int minMethodLines = 30,
        [Description("Minimum block size in lines to suggest extraction (default: 8)")] int minBlockLines = 8)
        => svc.ExtractMethodCandidates(projectName, minMethodLines, minBlockLines);
}