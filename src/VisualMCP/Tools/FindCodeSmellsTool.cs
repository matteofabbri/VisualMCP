using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class FindCodeSmellsTool
{
    [McpServerTool, Description("Detect common code smells via static analysis: async void, empty catch, await-in-lock, long methods, too many parameters, deep nesting, and large classes. Requires LoadSolution first.")]
    public static async Task<object> FindCodeSmells(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Max lines per method before flagging as 'long method' (default: 50)")] int maxMethodLines = 50,
        [Description("Max parameters before flagging (default: 5)")] int maxParams = 5,
        [Description("Max nesting depth before flagging (default: 4)")] int maxNesting = 4,
        [Description("Max public methods per class before flagging (default: 20)")] int maxPublicMethods = 20)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var smells = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                // ── async void ───────────────────────────────────────────
                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
                    if (method.ReturnType.ToString().Trim() != "void") continue;
                    // Allow event handlers (single parameter of EventArgs or subclass)
                    var sym = model.GetDeclaredSymbol(method) as IMethodSymbol;
                    if (sym is not null && IsEventHandler(sym)) continue;

                    Report("async void", method, document, project, smells,
                        "async void methods swallow exceptions; use async Task instead.");
                }

                // ── empty catch ───────────────────────────────────────────
                foreach (var @catch in root.DescendantNodes().OfType<CatchClauseSyntax>())
                {
                    var block = @catch.Block;
                    if (block.Statements.Count == 0 ||
                        block.Statements.All(s => s is EmptyStatementSyntax))
                        Report("empty catch", @catch, document, project, smells,
                            "Empty catch silently swallows exceptions.");
                }

                // ── await inside lock ─────────────────────────────────────
                foreach (var lockStmt in root.DescendantNodes().OfType<LockStatementSyntax>())
                {
                    if (lockStmt.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                        Report("await in lock", lockStmt, document, project, smells,
                            "await inside lock can cause deadlocks; use SemaphoreSlim instead.");
                }

                // ── long method / too many params / deep nesting ──────────
                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var span   = method.GetLocation().GetLineSpan();
                    var lines  = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
                    var @params = method switch
                    {
                        MethodDeclarationSyntax m      => m.ParameterList.Parameters.Count,
                        ConstructorDeclarationSyntax c => c.ParameterList.Parameters.Count,
                        _                              => 0,
                    };
                    var depth  = MaxNestingDepth(method);

                    if (lines > maxMethodLines)
                        Report("long method", method, document, project, smells,
                            $"Method has {lines} lines (threshold: {maxMethodLines}).");

                    if (@params > maxParams)
                        Report("too many parameters", method, document, project, smells,
                            $"Method has {@params} parameters (threshold: {maxParams}).");

                    if (depth > maxNesting)
                        Report("deep nesting", method, document, project, smells,
                            $"Method reaches nesting depth {depth} (threshold: {maxNesting}).");
                }

                // ── large class ───────────────────────────────────────────
                foreach (var cls in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var sym = model.GetDeclaredSymbol(cls) as INamedTypeSymbol;
                    if (sym is null) continue;
                    var publicMethods = sym.GetMembers()
                        .OfType<IMethodSymbol>()
                        .Count(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared);

                    if (publicMethods > maxPublicMethods)
                        Report("large class", cls, document, project, smells,
                            $"Class has {publicMethods} public methods (threshold: {maxPublicMethods}).");
                }
            }
        }

        var byKind = smells
            .GroupBy(s => ((dynamic)s).SmellKind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter = projectName ?? "all",
            totalSmells   = smells.Count,
            byKind,
            smells,
        };
    }

    private static void Report(string kind, SyntaxNode node, Document doc, Project project, List<object> list, string detail)
    {
        var loc = node.GetLocation().GetLineSpan();
        list.Add(new
        {
            SmellKind = kind,
            Detail    = detail,
            Project   = project.Name,
            FilePath  = doc.FilePath,
            Line      = loc.StartLinePosition.Line + 1,
        });
    }

    private static bool IsEventHandler(IMethodSymbol m)
    {
        if (m.Parameters.Length != 2) return false;
        var p1 = m.Parameters[0].Type.SpecialType == SpecialType.System_Object;
        var p2 = m.Parameters[1].Type.Name.EndsWith("EventArgs", StringComparison.Ordinal);
        return p1 && p2;
    }

    private static int MaxNestingDepth(SyntaxNode method)
    {
        int max = 0;
        void Walk(SyntaxNode n, int d)
        {
            if (d > max) max = d;
            foreach (var c in n.ChildNodes())
                Walk(c, c is BlockSyntax or SwitchSectionSyntax ? d + 1 : d);
        }
        Walk(method, 0);
        return max;
    }
}
