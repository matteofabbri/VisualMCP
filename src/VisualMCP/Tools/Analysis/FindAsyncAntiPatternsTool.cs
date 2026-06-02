using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Analysis;

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
    public static async Task<object> FindAsyncAntiPatterns(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Check for missing ConfigureAwait(false) — enable for library projects (default: false)")] bool checkConfigureAwait = false)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var findings = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                // ── .Result / .Wait() / .GetAwaiter().GetResult() blocking ────────────
                foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    var name = access.Name.Identifier.Text;
                    if (name != "Result" && name != "Wait" && name != "GetResult") continue;

                    var exprType = model.GetTypeInfo(access.Expression).Type;
                    if (exprType is null) continue;

                    bool isTask = IsTaskType(exprType);
                    if (!isTask) continue;

                    string antiPattern = name == "Result"    ? ".Result blocking call" :
                                         name == "Wait"      ? ".Wait() blocking call" :
                                                               ".GetAwaiter().GetResult() blocking call";

                    Report(antiPattern, access, document, project, findings,
                        $"Blocking on async code with {name} can cause deadlocks in sync contexts. Use await instead.");
                }

                // ── async void (non-event-handler) ─────────────────────────────────────
                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
                    if (method.ReturnType.ToString().Trim() != "void") continue;

                    var sym = model.GetDeclaredSymbol(method) as IMethodSymbol;
                    if (sym is null) continue;
                    if (IsEventHandler(sym)) continue;

                    Report("async void", method, document, project, findings,
                        "async void swallows exceptions. Use async Task instead.");
                }

                // ── await inside loop ───────────────────────────────────────────────────
                foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
                {
                    var parent = awaitExpr.Ancestors().FirstOrDefault(a =>
                        a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

                    if (parent is null) continue;

                    // Skip if there's an intervening lambda/anonymous method (that's fine)
                    var interveningFunc = awaitExpr.Ancestors()
                        .TakeWhile(a => a != parent)
                        .Any(a => a is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);

                    if (interveningFunc) continue;

                    var loopKind = parent switch
                    {
                        ForStatementSyntax     => "for",
                        ForEachStatementSyntax => "foreach",
                        WhileStatementSyntax   => "while",
                        DoStatementSyntax      => "do-while",
                        _                      => "loop",
                    };

                    Report($"await in {loopKind} loop", awaitExpr, document, project, findings,
                        "Sequential awaits in loops can be slow. Consider batching with Task.WhenAll or refactoring.");
                }

                // ── Task.Run wrapping synchronous-looking code ─────────────────────────
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax ma) continue;
                    if (ma.Name.Identifier.Text != "Run") continue;

                    var sym = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (sym is null) continue;
                    if (sym.ContainingType?.Name != "Task") continue;

                    // Flag Task.Run that wraps a non-async lambda (sync offload to thread pool)
                    var arg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (arg is LambdaExpressionSyntax lambda)
                    {
                        bool isAsync = lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                        if (!isAsync)
                            Report("Task.Run with sync lambda", invocation, document, project, findings,
                                "Task.Run offloads sync work to thread pool. If the work is truly CPU-bound this is fine; " +
                                "if it wraps blocking I/O, use async I/O instead.");
                    }
                }

                // ── missing ConfigureAwait(false) ─────────────────────────────────────
                if (checkConfigureAwait)
                {
                    foreach (var awaitExpr in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
                    {
                        // Skip if already has .ConfigureAwait(...)
                        if (awaitExpr.Expression is InvocationExpressionSyntax inv &&
                            inv.Expression is MemberAccessExpressionSyntax ma2 &&
                            ma2.Name.Identifier.Text == "ConfigureAwait")
                            continue;

                        var awaitedType = model.GetTypeInfo(awaitExpr.Expression).Type;
                        if (awaitedType is null) continue;
                        if (!IsTaskType(awaitedType)) continue;

                        Report("missing ConfigureAwait(false)", awaitExpr, document, project, findings,
                            "Library code should use .ConfigureAwait(false) to avoid capturing the synchronization context.");
                    }
                }
            }
        }

        var byKind = findings
            .GroupBy(f => ((dynamic)f).AntiPattern.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter        = projectName ?? "all",
            checkConfigureAwait,
            totalFindings        = findings.Count,
            byKind,
            findings,
        };
    }

    private static bool IsTaskType(ITypeSymbol type)
    {
        var name = type.Name;
        var ns   = type.ContainingNamespace?.ToDisplayString() ?? "";
        return ns == "System.Threading.Tasks" &&
               (name == "Task" || name == "ValueTask" || name.StartsWith("Task`") || name.StartsWith("ValueTask`"));
    }

    private static bool IsEventHandler(IMethodSymbol m)
    {
        if (m.Parameters.Length != 2) return false;
        return m.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
               m.Parameters[1].Type.Name.EndsWith("EventArgs", StringComparison.Ordinal);
    }

    private static void Report(string antiPattern, SyntaxNode node, Document doc, Project project, List<object> list, string detail)
    {
        var loc = node.GetLocation().GetLineSpan();
        list.Add(new
        {
            AntiPattern = antiPattern,
            Detail      = detail,
            Project     = project.Name,
            FilePath    = doc.FilePath,
            Line        = loc.StartLinePosition.Line + 1,
        });
    }
}
