using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class ExtractMethodCandidatesTool
{
    [McpServerTool, Description("Identify code blocks within long methods that are good candidates for extraction into separate methods: consecutive statements that form a cohesive unit (same local variables, single responsibility). Requires LoadSolution first.")]
    public static async Task<object> ExtractMethodCandidates(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Minimum method length in lines to analyse (default: 30)")] int minMethodLines = 30,
        [Description("Minimum block size in lines to suggest extraction (default: 8)")] int minBlockLines = 8)
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var suggestions = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var methodSpan = method.GetLocation().GetLineSpan();
                    var methodLines = methodSpan.EndLinePosition.Line - methodSpan.StartLinePosition.Line + 1;
                    if (methodLines < minMethodLines) continue;

                    var sym = model.GetDeclaredSymbol(method);
                    if (sym is null) continue;

                    var body = method.Body;
                    if (body is null) continue;

                    // Analyse groups of consecutive statements
                    var stmts = body.Statements.ToList();
                    var blocks = FindCohesiveBlocks(stmts, model, minBlockLines);

                    foreach (var block in blocks)
                    {
                        suggestions.Add(new
                        {
                            Method         = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            FullMethodName = sym.ToDisplayString(),
                            Project        = project.Name,
                            FilePath       = document.FilePath,
                            MethodLine     = methodSpan.StartLinePosition.Line + 1,
                            BlockStartLine = block.StartLine,
                            BlockEndLine   = block.EndLine,
                            BlockLines     = block.EndLine - block.StartLine + 1,
                            SharedLocals   = block.SharedLocals,
                            Reason         = block.Reason,
                        });
                    }
                }
            }
        }

        return new
        {
            projectFilter   = projectName ?? "all",
            minMethodLines,
            minBlockLines,
            suggestionCount = suggestions.Count,
            suggestions,
        };
    }

    private record BlockCandidate(int StartLine, int EndLine, List<string> SharedLocals, string Reason);

    private static List<BlockCandidate> FindCohesiveBlocks(
        List<StatementSyntax> statements,
        SemanticModel model,
        int minBlockLines)
    {
        var candidates = new List<BlockCandidate>();

        // Strategy 1: contiguous groups marked by blank lines / comment headers
        // Strategy 2: groups sharing the same local variables not used outside
        for (int start = 0; start < statements.Count - 1; start++)
        {
            for (int end = start + 1; end < statements.Count; end++)
            {
                var startLine = statements[start].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var endLine   = statements[end].GetLocation().GetLineSpan().EndLinePosition.Line + 1;

                if (endLine - startLine + 1 < minBlockLines) continue;

                // Collect locals declared in this range
                var blockStmts = statements[start..(end + 1)];
                var declaredLocals = blockStmts
                    .SelectMany(s => s.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    .Select(v => model.GetDeclaredSymbol(v)?.Name)
                    .Where(n => n is not null)
                    .ToList()!;

                if (declaredLocals.Count == 0) continue;

                // Check if declared locals are NOT used in statements outside this range
                var outerStmts = statements[..start].Concat(statements[(end + 1)..]).ToList();
                var usedOutside = outerStmts
                    .SelectMany(s => s.DescendantNodes().OfType<IdentifierNameSyntax>())
                    .Select(id => id.Identifier.Text)
                    .ToHashSet();

                var isolated = declaredLocals.Where(l => !usedOutside.Contains(l!)).ToList();
                if (isolated.Count == 0) continue;

                candidates.Add(new BlockCandidate(
                    startLine, endLine,
                    isolated!,
                    $"{isolated.Count} local(s) ({string.Join(", ", isolated.Take(3))}) are isolated to this block."));

                // Only report the largest non-overlapping block per start position
                break;
            }
        }

        // De-overlap: keep candidates that don't overlap with a larger one
        var sorted = candidates.OrderByDescending(c => c.EndLine - c.StartLine).ToList();
        var result = new List<BlockCandidate>();
        var covered = new HashSet<int>();
        foreach (var c in sorted)
        {
            var lines = Enumerable.Range(c.StartLine, c.EndLine - c.StartLine + 1).ToHashSet();
            if (lines.Any(l => covered.Contains(l))) continue;
            result.Add(c);
            foreach (var l in lines) covered.Add(l);
        }

        return result.OrderBy(c => c.StartLine).ToList();
    }
}
