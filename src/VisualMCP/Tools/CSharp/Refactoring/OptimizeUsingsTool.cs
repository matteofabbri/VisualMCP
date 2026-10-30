using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class OptimizeUsingsTool
{
    [McpServerTool, Description(
        "When you want to clean up 'using' directives, use this INSTEAD OF hand-pruning: Roslyn knows which usings are actually unused (text inspection cannot), " +
        "removes them, optionally sorts the rest, and writes to disk (ReSharper 'Optimize Usings'). " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> OptimizeUsings(
        IOptimizeUsingsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Sort remaining using directives alphabetically (default: true)")] bool sort = true,
        [Description("Dry run — report changes without writing to disk (default: false)")] bool dryRun = false)
        => svc.OptimizeUsings(projectName, filePath, sort, dryRun);
}
