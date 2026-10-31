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
        note = "All tools below require load_solution to have been called first, except razor_pages, " +
               "analyze_razor_pages and find_razor_vulnerabilities, which also accept an explicit path. " +
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
                description = "Returns the top-N most complex methods ranked by cognitive then cyclomatic complexity with risk labels (Low/Medium/High/Critical). Best first step before any refactoring.",
            },
            new
            {
                name        = "fix_complexity",
                trigger     = "Make the codebase simpler and you do not have a specific method in mind — the one call that ranks, reduces and reports",
                description = "Ranks every method by cyclomatic and cognitive complexity, then lowers the worst ones with the reducer (extract block, name the boolean, guard clause), re-measuring after each step and rolling back anything that would not compile. Whole solution by default, or one project/file. Use reduce_complexity instead when you already know the exact method.",
            },
            new
            {
                name        = "complexity_report",
                trigger     = "Why is this method complex, which line owns the score, what should I pull out of it — the cyclomatic/cognitive (not Big-O) view",
                description = "One method in full: McCabe cyclomatic, Sonar cognitive, nesting depth, Halstead volume, maintainability index, every decision point with line/nesting/cost, and the refactorings that would lower it. Omit filePath for the solution-wide ranking. Lambdas and local functions are their own units.",
            },
            new
            {
                name        = "reduce_complexity",
                trigger     = "Actually lower a method's complexity: apply the refactorings instead of only listing them",
                description = "Plans and applies the moves that remove decisions from a method (extract a nested block, name a tangled boolean, flatten a tail if into a guard clause), re-measures after each step and stops at the target. Compiler-gated per run with rollback; previews by default.",
            },
            new
            {
                name        = "analyze_complexity",
                trigger     = "Big-O time and space complexity, O(n^2) loops, recursion cost, memory growth, allocations in a loop, when something will get slow on larger input",
                description = "Estimates computational (time) and spatial (memory) Big-O per method: loop nesting and bounds, LINQ chains, sorting, linear lookups inside loops, recursion shape, allocation/materialisation sites and in-solution callees. Returns evidence, confidence and fix suggestions, worst first. A static heuristic — use profile_collect/profile_analyze for measured cost.",
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
                description = "Finds public/internal types and members with zero references in the solution, and reports separately the ones a framework calls by convention (frameworkInvoked). Slow on large solutions.",
            },
            new
            {
                name        = "find_reflection_usage",
                trigger     = "Is this symbol only reached by reflection? Activator/Type.GetType/GetMethod/Assembly.Load call sites",
                description = "Lists the reflective lookups with the name each one uses, and resolves the names that match a declaration in the solution. A resolved name is what makes a dead-code result safe to read.",
            },
            new
            {
                name        = "list_generated_sources",
                trigger     = "Is that type generated? Which source generators does the project use, and what did they emit",
                description = "Reports the generator references and the syntax trees they produced per project. Generated trees exist in the compilation but not as documents, so navigation and read_symbol cannot address them.",
            },
            new
            {
                name        = "find_external_usages",
                trigger     = "What do we use from a referenced library, which package members are actually called",
                description = "Reports the types and members of assemblies outside the solution that it uses, with call sites and a per-assembly summary. Framework assemblies are excluded unless includeFramework=true.",
            },
            new
            {
                name        = "framework_entry_points",
                trigger     = "Which members are called by a framework by convention (MVC actions, hub methods, hosted services, Blazor lifecycle, EF overrides, tests)",
                description = "Lists the framework-invoked members with the reason — the same classification find_unused_symbols applies. Use it to understand why a symbol with no references is not in the unused list.",
            },
            new
            {
                name        = "verify_change",
                trigger     = "Did this change alter behaviour? Prove it with the tests instead of reading the diff",
                description = "Runs the suite on the working tree and on a git revision in a detached worktree and diffs the outcomes test by test: regressions, fixes, new/removed tests, verdict. The runtime half of analysis.",
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
            new
            {
                name        = "razor_pages",
                trigger     = "Inventory of Razor Pages, MVC views, layouts, partials and Blazor components; which page has which route, model, layout and handlers",
                description = "Lists every .cshtml/.razor file with its kind, @page route, @model, layout, sections, @inject services, handlers, code-behind and markup counts. Read-only; also accepts an explicit path.",
            },
            new
            {
                name        = "analyze_razor_pages",
                trigger     = "Problems in Razor markup: broken HTML, accessibility, ViewBag, legacy helpers, oversized code blocks",
                description = "Runs the RZ2xxx rules with rule id, severity, file:line, snippet and a suggested fix. Excludes security findings, which are reported by find_razor_vulnerabilities.",
            },
            new
            {
                name        = "find_razor_vulnerabilities",
                trigger     = "Security audit of Razor pages: XSS, CSRF, Html.Raw, inline handlers, javascript: URLs, secrets, CSP",
                description = "Runs the RZ1xxx security rules and returns CWE-tagged findings (CWE-79, CWE-352, CWE-798, …) with severity, file:line and remediation.",
            },
        },
    };
}
