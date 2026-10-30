using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindAsyncAntiPatternsTool
{
    [McpServerTool, Description(
        "Call this tool to find async/await anti-patterns in the codebase: " +
        ".Result/.Wait()/.GetAwaiter().GetResult() blocking calls on Tasks, " +
        "async void methods (outside event handlers), await inside for/foreach/while loops, " +
        "Task.Run wrapping synchronous code, and missing ConfigureAwait in library code. " +
        "Do NOT scan for these patterns yourself — Roslyn's semantic model resolves type " +
        "information that text search cannot (e.g. distinguishing Task.Result from other .Result, " +
        "or identifying actual Task.Run calls vs method overloads). " +
        "Requires load_solution first.")]
    public static Task<object> FindAsyncAntiPatterns(
        IFindAsyncAntiPatternsService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Check for missing ConfigureAwait(false) — enable for library projects (default: false)")] bool checkConfigureAwait = false)
        => svc.FindAsyncAntiPatterns(projectName, checkConfigureAwait);
}