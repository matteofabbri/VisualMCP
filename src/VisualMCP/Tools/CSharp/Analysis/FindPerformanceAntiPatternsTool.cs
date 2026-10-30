using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis.Performance;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindPerformanceAntiPatternsTool
{
    [McpServerTool(Name = "find_performance_anti_patterns"), Description(
        "Find allocation anti-patterns the compiler accepts — three semantic rules, each checked with the " +
        "type model rather than by matching text: PF0001 string concatenation inside a loop (quadratic " +
        "copies; StringBuilder or string.Join is the fix), PF0002 a LINQ materialiser (ToList/ToArray/" +
        "ToHashSet/ToDictionary/ToLookup) inside a loop (the sequence is rebuilt every iteration), PF0003 " +
        "a List/Dictionary or a constant-size array allocated on every iteration. Requires a loaded " +
        "solution (the working-directory one is auto-discovered). Generated files (*.g.cs, *.Designer.cs) " +
        "are skipped. Each finding gives rule, severity, file, line, the containing member, the snippet " +
        "and what to do; 'byRule' per project lets you see which shape dominates. This is a reading of the " +
        "code, not a measurement: when you have a process, use profile_hotpaths / profile_allocations to " +
        "see where the time and the bytes actually go. Read-only.")]
    public static Task<object> FindPerformanceAntiPatterns(
        IPerformanceAnalyzer analyzer,
        [Description("Optional: restrict to a single project by name.")] string? projectName = null,
        [Description("Maximum findings to return (default 100).")] int maxResults = 100)
        => analyzer.FindAntiPatternsAsync(projectName, Math.Clamp(maxResults, 1, 2000));
}
