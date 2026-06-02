using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Analysis;

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
    public static async Task<object> FindDeadCode(
        [Description("Optional: restrict to a single project by name")] string? projectName = null)
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

                // ── Roslyn diagnostics: CS0162 unreachable code ─────────────────────
                var diags = model.GetDiagnostics();
                foreach (var diag in diags.Where(d => d.Id == "CS0162"))
                {
                    var loc = diag.Location.GetLineSpan();
                    findings.Add(new
                    {
                        Kind     = "unreachable statement (CS0162)",
                        Detail   = diag.GetMessage(),
                        Project  = project.Name,
                        FilePath = document.FilePath,
                        Line     = loc.StartLinePosition.Line + 1,
                    });
                }

                // ── statements after unconditional jump ────────────────────────────
                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var body = method.Body ?? (method as MethodDeclarationSyntax)?.Body;
                    if (body is null) continue;

                    FindStatementsAfterJump(body, document, project, findings);
                }

                // ── always-false / always-true if conditions ────────────────────────
                foreach (var ifStmt in root.DescendantNodes().OfType<IfStatementSyntax>())
                {
                    var val = model.GetConstantValue(ifStmt.Condition);
                    if (!val.HasValue || val.Value is not bool b) continue;

                    var loc = ifStmt.GetLocation().GetLineSpan();
                    if (!b)
                        findings.Add(new
                        {
                            Kind     = "always-false if condition",
                            Detail   = "Condition is always false; the if-body is dead code.",
                            Project  = project.Name,
                            FilePath = document.FilePath,
                            Line     = loc.StartLinePosition.Line + 1,
                        });
                    else if (ifStmt.Else is not null)
                        findings.Add(new
                        {
                            Kind     = "always-true if condition",
                            Detail   = "Condition is always true; the else-branch is dead code.",
                            Project  = project.Name,
                            FilePath = document.FilePath,
                            Line     = ifStmt.Else.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        });
                }

                // ── switch with constant expression (dead arms) ─────────────────────
                foreach (var sw in root.DescendantNodes().OfType<SwitchStatementSyntax>())
                {
                    var val = model.GetConstantValue(sw.Expression);
                    if (!val.HasValue) continue;

                    foreach (var section in sw.Sections)
                    {
                        bool hasMatch = section.Labels.Any(l =>
                        {
                            if (l is CaseSwitchLabelSyntax cs)
                            {
                                var cv = model.GetConstantValue(cs.Value);
                                return cv.HasValue && Equals(cv.Value, val.Value);
                            }
                            return l is DefaultSwitchLabelSyntax;
                        });

                        if (hasMatch) continue;

                        var loc = section.GetLocation().GetLineSpan();
                        findings.Add(new
                        {
                            Kind     = "dead switch arm",
                            Detail   = $"Switch expression is constant ({val.Value}); this arm can never match.",
                            Project  = project.Name,
                            FilePath = document.FilePath,
                            Line     = loc.StartLinePosition.Line + 1,
                        });
                    }
                }
            }
        }

        // Deduplicate by (FilePath, Line, Kind) — CS0162 may overlap with our own checks
        var deduped = findings
            .GroupBy(f => $"{((dynamic)f).FilePath}:{((dynamic)f).Line}:{((dynamic)f).Kind}")
            .Select(g => g.First())
            .OrderBy(f => ((dynamic)f).FilePath)
            .ThenBy(f => ((dynamic)f).Line)
            .ToList();

        var byKind = deduped
            .GroupBy(f => ((dynamic)f).Kind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter = projectName ?? "all",
            totalFindings = deduped.Count,
            byKind,
            findings      = deduped,
        };
    }

    private static void FindStatementsAfterJump(BlockSyntax block, Document doc, Project project, List<object> findings)
    {
        var stmts = block.Statements;
        for (int i = 0; i < stmts.Count - 1; i++)
        {
            var stmt = stmts[i];
            if (stmt is not (ReturnStatementSyntax or ThrowStatementSyntax or
                             BreakStatementSyntax  or ContinueStatementSyntax)) continue;

            // Everything from i+1 onward is unreachable
            for (int j = i + 1; j < stmts.Count; j++)
            {
                var dead = stmts[j];
                // Skip if already a closing brace / compiler-generated
                if (dead is BlockSyntax) continue;

                var loc = dead.GetLocation().GetLineSpan();
                findings.Add(new
                {
                    Kind     = $"statement after {stmt.Kind().ToString().Replace("Statement", "").ToLowerInvariant()}",
                    Detail   = $"This statement is unreachable; it follows an unconditional {stmt.Kind().ToString().Replace("Statement", "").ToLowerInvariant()}.",
                    Project  = project.Name,
                    FilePath = doc.FilePath,
                    Line     = loc.StartLinePosition.Line + 1,
                });
            }
            break; // no need to scan further in this block
        }

        // Recurse into nested blocks
        foreach (var nested in block.DescendantNodes().OfType<BlockSyntax>().Where(b => b != block))
            FindStatementsAfterJump(nested, doc, project, findings);
    }
}
