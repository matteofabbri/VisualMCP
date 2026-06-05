using ModelContextProtocol.Server;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class ListAnalysisToolsTool
{
    [McpServerTool, Description(
        "Call this tool at the start of any code analysis, quality review, or refactoring session " +
        "to get the complete catalogue of available analysis tools, what each one does, and when to call it. " +
        "Always call this tool first when the user asks to 'analyse', 'review', 'check quality', " +
        "'find problems', or 'refactor' — it tells you exactly which tool to use for each task " +
        "so you do not attempt the analysis yourself.")]
    public static object ListAnalysisTools() => new
    {
        note = "All tools below require load_solution to have been called first. " +
               "Call check_solution if you are unsure whether a solution is loaded.",
        tools = new[]
        {
            new
            {
                name        = "get_diagnostics",
                trigger     = "Compiler errors, warnings, or code diagnostics",
                description = "Returns all Roslyn compiler diagnostics (errors and warnings) for the loaded solution or a specific project.",
            },
            new
            {
                name        = "get_metrics",
                trigger     = "Cyclomatic complexity, lines of code, or nesting depth for specific methods",
                description = "Computes per-method code metrics: cyclomatic complexity, LOC, nesting depth, parameter count. Use get_complexity_hotspots for a solution-wide top-N ranking.",
            },
            new
            {
                name        = "get_complexity_hotspots",
                trigger     = "Starting a refactoring session, finding the most complex / hardest-to-maintain methods in the solution",
                description = "Returns the top-N most complex methods ranked by cyclomatic complexity with risk labels (Low/Medium/High/Critical). Best first step before any refactoring.",
            },
            new
            {
                name        = "find_code_smells",
                trigger     = "General code quality review, async void, empty catch, deep nesting, long methods, large classes",
                description = "Detects classic code smells via static analysis: async void, empty catch, await-in-lock, long methods, too many parameters, deep nesting, large classes.",
            },
            new
            {
                name        = "find_async_anti_patterns",
                trigger     = "Async/await issues, deadlocks, .Result/.Wait() blocking calls, Task.Run misuse",
                description = "Finds async/await anti-patterns: .Result/.Wait()/GetResult() blocking, async void, await in loops, Task.Run with sync lambdas, missing ConfigureAwait.",
            },
            new
            {
                name        = "find_dead_code",
                trigger     = "Unreachable code, statements after return/throw, always-false conditions, dead switch arms",
                description = "Detects unreachable code via Roslyn control-flow analysis: CS0162 diagnostics, statements after unconditional jumps, constant if/switch conditions.",
            },
            new
            {
                name        = "find_unused_symbols",
                trigger     = "Unused public API, dead types or methods, candidates for removal",
                description = "Finds public/internal types and members with zero references in the solution. Slow on large solutions.",
            },
            new
            {
                name        = "analyze_dependencies",
                trigger     = "Project reference graph, which projects depend on which, project-level circular references",
                description = "Builds the project dependency graph, detects project-level cycles, and identifies root projects.",
            },
            new
            {
                name        = "analyze_circular_namespace_dependencies",
                trigger     = "Namespace-level circular dependencies, architecture violations between layers",
                description = "Detects circular dependencies between namespaces by analysing actual type usage (not just using directives). More granular than analyze_dependencies.",
            },
            new
            {
                name        = "analyze_namespace_coupling",
                trigger     = "Coupling metrics, instability, Robert Martin metrics, architecture health, distance from main sequence",
                description = "Computes per-namespace: afferent coupling (Ca), efferent coupling (Ce), instability (Ce/Ca+Ce), abstractness, and distance from the main sequence.",
            },
            new
            {
                name        = "find_undocumented_public_api",
                trigger     = "Missing XML documentation, undocumented public API",
                description = "Lists public types and members that lack XML documentation comments.",
            },
        },
    };
}
