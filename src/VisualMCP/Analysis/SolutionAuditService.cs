using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Workspace;

namespace VisualMCP.Analysis;

/// <summary>One row of the "worst offenders" tables.</summary>
public sealed record AuditRow(string Key, int Count, string Example);

/// <summary>
/// Whole-solution health report in a single call: analyzer-pack findings, compiler diagnostics,
/// complexity hotspots, file sizes and the project graph. Read-only; composes what the individual
/// analysis tools already know, so a review does not need a dozen round-trips.
/// </summary>
public interface ISolutionAuditService
{
    Task<object> AuditAsync(
        string? project = null,
        int maxPerCategory = 10,
        bool includePacks = true,
        string minSeverity = "WARNING",
        CancellationToken cancellationToken = default);
}

public sealed class SolutionAuditService : ISolutionAuditService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IInspectCodeService _inspections;

    public SolutionAuditService(IRoslynWorkspace workspace, IInspectCodeService inspections)
    {
        _workspace = workspace;
        _inspections = inspections;
    }

    public async Task<object> AuditAsync(
        string? project = null,
        int maxPerCategory = 10,
        bool includePacks = true,
        string minSeverity = "WARNING",
        CancellationToken cancellationToken = default)
    {
        maxPerCategory = Math.Clamp(maxPerCategory, 1, 50);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var projects = solution.Projects
            .Where(candidate => candidate.Language == LanguageNames.CSharp)
            .Where(candidate => project is null || candidate.Name.Equals(project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projects.Count == 0)
            return new { error = project is null ? "The loaded solution has no C# project." : $"Project '{project}' not found." };

        // ── Compiler diagnostics, complexity and file sizes, per project ───────────────────
        var errors = new Dictionary<string, (int Count, string Example)>(StringComparer.OrdinalIgnoreCase);
        var warnings = new Dictionary<string, (int Count, string Example)>(StringComparer.OrdinalIgnoreCase);
        var hotspots = new List<(int Complexity, int Lines, string Method, string File, int Line)>();
        var files = new List<(int Lines, string File)>();
        var errorTotal = 0;
        var warningTotal = 0;
        var linesOfCode = 0;

        foreach (var current in projects)
        {
            var compilation = await current.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken)
                         .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                         .Where(d => d.Location.IsInSource))
            {
                var example = $"{Path.GetFileName(diagnostic.Location.SourceTree?.FilePath)}:{LineOf(diagnostic.Location)}";
                var bucket = diagnostic.Severity == DiagnosticSeverity.Error ? errors : warnings;
                if (diagnostic.Severity == DiagnosticSeverity.Error) errorTotal++;
                else warningTotal++;

                bucket[diagnostic.Id] = bucket.TryGetValue(diagnostic.Id, out var existing)
                    ? (existing.Count + 1, existing.Example)
                    : (1, example);
            }

            foreach (var document in current.Documents)
            {
                var text = await document.GetTextAsync(cancellationToken);
                var fileLines = text.Lines.Count;
                linesOfCode += fileLines;
                files.Add((fileLines, document.FilePath ?? document.Name));

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null) continue;

                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var complexity = 1 + method.DescendantNodes().Count(IsDecisionPoint);
                    var span = method.GetLocation().GetLineSpan();
                    var lineCount = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
                    hotspots.Add((complexity, lineCount,
                        DescribeMethod(method),
                        Path.GetFileName(document.FilePath ?? document.Name),
                        span.StartLinePosition.Line + 1));
                }
            }
        }

        // ── Analyzer packs (house / Roslynator / CA / async / meziantou) ──────────────────
        InspectionReport? packReport = null;
        if (includePacks)
        {
            packReport = await _inspections.RunAsync(
                projectName: project,
                filePath: null,
                packs: "house,roslynator,netanalyzers,asyncfixer,meziantou",
                minSeverity: minSeverity,
                ids: null,
                excludeIds: null,
                enableAll: true,
                maxResults: 5_000,
                timeoutSeconds: 300);
        }

        var packRows = packReport?.Findings
            .GroupBy(finding => finding.InspectionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AuditRow(
                group.Key,
                group.Count(),
                $"{Path.GetFileName(group.First().File)}:{group.First().Line} — {Truncate(group.First().Message, 90)}"))
            .OrderByDescending(row => row.Count)
            .ToList() ?? new List<AuditRow>();

        // ── Project graph ─────────────────────────────────────────────────────────────────
        var graph = solution.Projects.ToDictionary(
            candidate => candidate.Name,
            candidate => candidate.ProjectReferences
                .Select(reference => solution.GetProject(reference.ProjectId)?.Name)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var cycles = FindCycles(graph);

        // ── Report ────────────────────────────────────────────────────────────────────────
        var builder = new StringBuilder();
        builder.AppendLine($"Solution: {Path.GetFileName(solution.FilePath)} — {projects.Count} project(s), " +
                           $"{files.Count} file(s), {linesOfCode:N0} line(s)");

        if (packReport is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"Inspections (packs: {string.Join(", ", packReport.Packs)}): " +
                               $"{packReport.Total} finding(s) in {packReport.ElapsedMs} ms" +
                               (packReport.Truncated ? " — TRUNCATED" : string.Empty));
            if (packReport.Error is not null) builder.AppendLine("  error: " + packReport.Error);

            builder.AppendLine("  by severity: " +
                string.Join(", ", packReport.BySeverity.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key} {pair.Value}")));

            foreach (var row in packRows.Take(maxPerCategory))
                builder.AppendLine($"  {row.Count,4}x {row.Key,-10} {row.Example}");
            if (packRows.Count > maxPerCategory)
                builder.AppendLine($"  … {packRows.Count - maxPerCategory} more inspection id(s)");
        }

        builder.AppendLine();
        builder.AppendLine($"Compiler: {errorTotal} error(s), {warningTotal} warning(s)");
        foreach (var (id, entry) in errors.OrderByDescending(pair => pair.Value.Count).Take(maxPerCategory))
            builder.AppendLine($"  {entry.Count,4}x {id,-10} error at {entry.Example}");
        foreach (var (id, entry) in warnings.OrderByDescending(pair => pair.Value.Count).Take(maxPerCategory))
            builder.AppendLine($"  {entry.Count,4}x {id,-10} warning at {entry.Example}");

        builder.AppendLine();
        builder.AppendLine("Most complex methods (cyclomatic complexity, method length):");
        foreach (var hotspot in hotspots.OrderByDescending(entry => entry.Complexity).ThenByDescending(entry => entry.Lines).Take(maxPerCategory))
            builder.AppendLine($"  {hotspot.Complexity,3} complexity {hotspot.Lines,4} lines  {hotspot.Method}  ({hotspot.File}:{hotspot.Line})");

        builder.AppendLine();
        builder.AppendLine("Largest files:");
        foreach (var file in files.OrderByDescending(entry => entry.Lines).Take(maxPerCategory))
            builder.AppendLine($"  {file.Lines,5} lines  {file.File}");

        builder.AppendLine();
        builder.AppendLine("Project graph:");
        foreach (var (name, references) in graph.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"  {name,-32} → {(references.Count == 0 ? "(leaf)" : string.Join(", ", references))}");
        if (cycles.Count > 0)
            builder.AppendLine($"  CYCLES: {string.Join(" | ", cycles)}");

        builder.AppendLine();
        builder.AppendLine("Suggested first moves:");
        var suggestions = 0;
        if (errorTotal > 0)
        {
            builder.AppendLine($"  1. Fix the {errorTotal} compiler error(s): nothing else can be trusted until it builds.");
            suggestions++;
        }

        var packErrors = packReport?.Findings.Count(finding => finding.Severity == "ERROR") ?? 0;
        if (packErrors > 0)
        {
            builder.AppendLine($"  {suggestions + 1}. {packErrors} inspection(s) at ERROR severity — apply_code_fix can repair the ones with a fixer.");
            suggestions++;
        }

        var worst = hotspots.OrderByDescending(entry => entry.Complexity).FirstOrDefault();
        if (worst.Complexity >= 10)
        {
            builder.AppendLine($"  {suggestions + 1}. Extract from '{worst.Method}' (complexity {worst.Complexity}) — extract_method on its worst block.");
            suggestions++;
        }

        if (cycles.Count > 0)
            builder.AppendLine($"  {suggestions + 1}. Break {cycles.Count} project reference cycle(s) — the build order is undefined.");

        if (suggestions == 0)
            builder.AppendLine("  Nothing urgent: no errors, no ERROR-severity inspections, no hotspot above complexity 10.");

        return new
        {
            solution = solution.FilePath,
            projects = projects.Count,
            files = files.Count,
            linesOfCode,
            compilerErrors = errorTotal,
            compilerWarnings = warningTotal,
            inspections = packReport?.Total ?? 0,
            inspectionIds = packRows.Count,
            projectCycles = cycles.Count,
            topComplexity = hotspots.Count == 0 ? 0 : hotspots.Max(entry => entry.Complexity),
            report = builder.ToString().TrimEnd(),
        };
    }

    private static bool IsDecisionPoint(SyntaxNode node) => node switch
    {
        IfStatementSyntax => true,
        ForEachStatementSyntax => true,
        ForStatementSyntax => true,
        WhileStatementSyntax => true,
        DoStatementSyntax => true,
        CatchClauseSyntax => true,
        ConditionalExpressionSyntax => true,
        CaseSwitchLabelSyntax => true,
        CasePatternSwitchLabelSyntax => true,
        BinaryExpressionSyntax binary => binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                                         binary.IsKind(SyntaxKind.LogicalOrExpression) ||
                                         binary.IsKind(SyntaxKind.CoalesceExpression),
        _ => false,
    };

    private static string DescribeMethod(BaseMethodDeclarationSyntax method) => method switch
    {
        MethodDeclarationSyntax m => $"{m.Identifier.ValueText}({m.ParameterList.Parameters.Count} args)",
        ConstructorDeclarationSyntax c => $"{c.Identifier.ValueText} ctor",
        DestructorDeclarationSyntax d => $"{d.Identifier.ValueText} dtor",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.ValueText}",
        _ => method.Kind().ToString(),
    };

    private static List<string> FindCycles(Dictionary<string, List<string>> graph)
    {
        var cycles = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        void Walk(string node)
        {
            if (done.Contains(node)) return;
            if (!visiting.Add(node))
            {
                var start = path.FindIndex(entry => entry.Equals(node, StringComparison.OrdinalIgnoreCase));
                if (start >= 0) cycles.Add(string.Join(" → ", path.Skip(start).Append(node)));
                return;
            }

            path.Add(node);
            foreach (var next in graph.TryGetValue(node, out var references) ? references : new List<string>())
                Walk(next);
            path.RemoveAt(path.Count - 1);
            visiting.Remove(node);
            done.Add(node);
        }

        foreach (var node in graph.Keys) Walk(node);
        return cycles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int LineOf(Location location) =>
        location.GetLineSpan().StartLinePosition.Line + 1;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
