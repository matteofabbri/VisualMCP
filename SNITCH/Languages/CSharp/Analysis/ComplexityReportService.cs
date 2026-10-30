using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IComplexityReportService
{
    Task<object> ComplexityReport(
        string? filePath,
        int? line,
        string? methodName,
        string? projectName,
        int topN,
        int minComplexity,
        bool includeContributors,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same ranking as <see cref="ComplexityReport"/> with no <c>filePath</c>, returned as data.
    /// The orchestrator reads it to decide which methods to lower, instead of parsing tool JSON.
    /// </summary>
    Task<ComplexityRanking> RankMetricsAsync(
        string? projectName = null,
        string? filePath = null,
        int minComplexity = 5,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Every unit at or above the threshold, worst first, plus an error channel. <see cref="Metrics"/>
/// is the full list (the ranking is not truncated) so a caller can count what is left.
/// </summary>
public sealed record ComplexityRanking(IReadOnlyList<ComplexityMetrics> Metrics, string? Error = null);

/// <summary>
/// The drill-down behind <c>complexity_report</c>: either one method in full — every decision point
/// with its line, nesting level and cost, plus the moves that would lower it — or the solution's
/// worst methods ranked by cognitive complexity.
/// <para>
/// This is the structural metric (McCabe paths, Sonar cognitive score), not the algorithmic one:
/// <c>analyze_complexity</c> answers "how fast does this grow with n", this answers "how many paths
/// does a reader have to hold".
/// </para>
/// </summary>
public sealed class ComplexityReportService : IComplexityReportService
{
    private static readonly string[] Thresholds =
    [
        "cyclomatic: 10 medium / 15 high / 25 critical",
        "cognitive: 10 medium / 15 high (Sonar's default limit) / 25 critical",
        "maintainability index: 100 is clean, under 20 is hard to maintain",
    ];

    private readonly IRoslynWorkspace _workspace;

    public ComplexityReportService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ComplexityReport(
        string? filePath = null,
        int? line = null,
        string? methodName = null,
        string? projectName = null,
        int topN = 20,
        int minComplexity = 5,
        bool includeContributors = true,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        if (!string.IsNullOrWhiteSpace(filePath))
            return await ReportMethodAsync(solution, filePath!, line, methodName, includeContributors, cancellationToken);

        return await RankAsync(solution, projectName, topN, minComplexity, includeContributors, cancellationToken);
    }

    /// <summary>One method, in full: the score, what owns it, and how to lower it.</summary>
    private async Task<object> ReportMethodAsync(
        Solution solution,
        string filePath,
        int? line,
        string? methodName,
        bool includeContributors,
        CancellationToken cancellationToken)
    {
        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, HostSystem.PathComparison));

        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        var units = ComplexityAnalyzer.Units(root).ToList();
        var unit = Locate(units, line, methodName);

        if (unit is null)
            return new
            {
                error = line is not null
                    ? $"No method, lambda or local function covers line {line}."
                    : methodName is not null
                        ? $"No unit named '{methodName}' in {Path.GetFileName(path)}."
                        : "Say which method: pass line (any line inside it) or methodName.",
                units = units
                    .Select(candidate => ComplexityAnalyzer.Analyze(candidate, model, document.FilePath))
                    .Select(metrics => new { metrics.Name, metrics.Line, metrics.Cognitive, metrics.Cyclomatic })
                    .Take(50)
                    .ToList(),
            };

        var metrics = ComplexityAnalyzer.Analyze(unit, model, document.FilePath);
        var suggestions = ComplexityPlanner.Suggest(unit, model, cancellationToken);

        return new
        {
            metrics.Name,
            metrics.FullName,
            metrics.FilePath,
            metrics.Line,
            metrics.EndLine,
            metrics.Cyclomatic,
            metrics.Cognitive,
            metrics.MaxNesting,
            metrics.LinesOfCode,
            metrics.ParameterCount,
            metrics.HalsteadVolume,
            metrics.MaintainabilityIndex,
            metrics.Risk,
            thresholds = Thresholds,
            Contributors = includeContributors
                ? metrics.WorstContributions(25)
                    .Select(contribution => new
                    {
                        contribution.Kind,
                        contribution.Line,
                        contribution.Nesting,
                        contribution.Cyclomatic,
                        contribution.Cognitive,
                        contribution.Detail,
                    })
                    .ToList()
                : null,
            Suggestions = suggestions.Select(suggestion => new
            {
                suggestion.Kind,
                suggestion.Line,
                suggestion.EndLine,
                suggestion.Description,
                suggestion.CyclomaticReduction,
                suggestion.CognitiveReduction,
                suggestion.AutoApplicable,
                suggestion.BlockedBy,
            }),
            note = suggestions.Any(suggestion => suggestion.AutoApplicable)
                ? "reduce_complexity can apply the auto-applicable moves on this method."
                : "Nothing here is safe to rewrite automatically; the suggestions need a human.",
        };
    }

    /// <summary>The solution's worst methods, ranked the way a maintainability review reads them.</summary>
    private async Task<object> RankAsync(
        Solution solution,
        string? projectName,
        int topN,
        int minComplexity,
        bool includeContributors,
        CancellationToken cancellationToken)
    {
        var (results, error) = await CollectAsync(solution, projectName, filePath: null, minComplexity, cancellationToken);
        if (error is not null) return new { error };

        var ranked = results.Take(topN).ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            topN,
            minComplexity,
            totalAboveThreshold = results.Count,
            distribution = new
            {
                Critical = results.Count(metrics => metrics.Risk == "Critical"),
                High = results.Count(metrics => metrics.Risk == "High"),
                Medium = results.Count(metrics => metrics.Risk == "Medium"),
                Low = results.Count(metrics => metrics.Risk == "Low"),
            },
            methods = ranked.Select(metrics => new
            {
                metrics.FullName,
                metrics.FilePath,
                metrics.Line,
                metrics.Cyclomatic,
                metrics.Cognitive,
                metrics.MaxNesting,
                metrics.LinesOfCode,
                metrics.MaintainabilityIndex,
                metrics.Risk,
                worstContributors = includeContributors
                    ? metrics.WorstContributions(3)
                        .Select(contribution => $"{contribution.Kind} at line {contribution.Line} (+{contribution.Cognitive} cognitive, nesting {contribution.Nesting})")
                        .ToList()
                    : null,
            }),
        };
    }

    /// <inheritdoc />
    public async Task<ComplexityRanking> RankMetricsAsync(
        string? projectName = null,
        string? filePath = null,
        int minComplexity = 5,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new ComplexityRanking([], "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx.");

        var (metrics, error) = await CollectAsync(solution, projectName, filePath, minComplexity, cancellationToken);
        return new ComplexityRanking(metrics, error);
    }

    /// <summary>
    /// Every unit at or above <paramref name="minComplexity"/> (cognitive OR cyclomatic), worst first.
    /// One implementation for the tool's ranking and for the orchestrator that lowers it, so the two
    /// cannot disagree on which methods are the worst.
    /// </summary>
    private static async Task<(List<ComplexityMetrics> Metrics, string? Error)> CollectAsync(
        Solution solution,
        string? projectName,
        string? filePath,
        int minComplexity,
        CancellationToken cancellationToken)
    {
        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return ([], $"Project '{projectName}' not found.");

        string? wantedFile = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try { wantedFile = Path.GetFullPath(filePath); }
            catch (Exception ex) { return ([], $"Invalid path '{filePath}': {ex.Message}"); }
        }

        var results = new List<ComplexityMetrics>();
        var foundFile = wantedFile is null;

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(candidate => candidate.SourceCodeKind == SourceCodeKind.Regular))
            {
                if (wantedFile is not null)
                {
                    if (document.FilePath is null ||
                        !string.Equals(Path.GetFullPath(document.FilePath), wantedFile, HostSystem.PathComparison))
                        continue;

                    foundFile = true;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null) continue;

                foreach (var unit in ComplexityAnalyzer.Units(root))
                {
                    var metrics = ComplexityAnalyzer.Analyze(unit, model, document.FilePath);
                    if (metrics.Cognitive < minComplexity && metrics.Cyclomatic < minComplexity) continue;
                    results.Add(metrics);
                }
            }
        }

        if (!foundFile) return ([], $"{wantedFile} is not a document of the loaded solution.");

        return (results
            .OrderByDescending(metrics => metrics.Cognitive)
            .ThenByDescending(metrics => metrics.Cyclomatic)
            .ToList(), null);
    }

    private static SyntaxNode? Locate(IReadOnlyList<SyntaxNode> units, int? line, string? methodName)
    {
        if (line is not null)
            return units
                .Where(candidate => StartLine(candidate) <= line && EndLine(candidate) >= line)
                .OrderBy(candidate => candidate.Span.Length)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(methodName)) return units.Count == 1 ? units[0] : null;

        return units.FirstOrDefault(candidate => NameOf(candidate).Equals(methodName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NameOf(SyntaxNode unit) => unit switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
        LocalFunctionStatementSyntax local => local.Identifier.ValueText,
        AccessorDeclarationSyntax accessor => accessor.Keyword.Text,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        AnonymousFunctionExpressionSyntax => "lambda",
        _ => unit.Kind().ToString(),
    };

    private static int StartLine(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
}
