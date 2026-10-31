using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IGetMetricsService
{
    Task<object> GetMetrics(string? projectName, int minComplexity);
}

public sealed class GetMetricsService : IGetMetricsService
{
    private readonly IRoslynWorkspace _workspace;
    public GetMetricsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> GetMetrics(
        string? projectName = null,
        int minComplexity = 1)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var allMetrics = new List<object>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                var model = await document.GetSemanticModelAsync();

                foreach (var method in ComplexityAnalyzer.MethodUnits(root))
                {
                    var sym = model?.GetDeclaredSymbol(method);
                    if (sym is null) continue;

                    var metrics = ComplexityAnalyzer.Analyze(method, model, document.FilePath);
                    if (metrics.Cyclomatic < minComplexity && metrics.Cognitive < minComplexity) continue;

                    allMetrics.Add(new
                    {
                        Project             = project.Name,
                        Method              = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        FullName            = sym.ToDisplayString(),
                        document.FilePath,
                        metrics.Line,
                        CyclomaticComplexity = metrics.Cyclomatic,
                        CognitiveComplexity  = metrics.Cognitive,
                        LinesOfCode         = metrics.LinesOfCode,
                        MaxNestingDepth     = metrics.MaxNesting,
                        ParameterCount      = metrics.ParameterCount,
                        MaintainabilityIndex = metrics.MaintainabilityIndex,
                    });
                }
            }
        }

        allMetrics = allMetrics.OrderByDescending(m => ((dynamic)m).CognitiveComplexity).ToList();

        return new
        {
            projectFilter = projectName ?? "all",
            minComplexity,
            methodCount   = allMetrics.Count,
            metrics       = allMetrics,
        };
    }
}
