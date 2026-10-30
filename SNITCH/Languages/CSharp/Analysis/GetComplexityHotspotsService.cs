using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IGetComplexityHotspotsService
{
    Task<object> GetComplexityHotspots(string? projectName, int topN, int minComplexity);
}

public sealed class GetComplexityHotspotsService : IGetComplexityHotspotsService
{
    private readonly IRoslynWorkspace _workspace;
    public GetComplexityHotspotsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> GetComplexityHotspots(
        string? projectName = null,
        int topN = 20,
        int minComplexity = 5)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var results = new List<(int Cognitive, int Cc, object Entry)>();

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular))
            {
                var root  = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    var sym = model.GetDeclaredSymbol(method);
                    if (sym is null) continue;

                    var metrics = ComplexityAnalyzer.Analyze(method, model, document.FilePath);
                    if (metrics.Cyclomatic < minComplexity && metrics.Cognitive < minComplexity) continue;

                    results.Add((metrics.Cognitive, metrics.Cyclomatic, new
                    {
                        Rank                 = 0, // filled below
                        RiskLabel            = metrics.Risk,
                        CyclomaticComplexity = metrics.Cyclomatic,
                        CognitiveComplexity  = metrics.Cognitive,
                        MaxNesting           = metrics.MaxNesting,
                        MaintainabilityIndex = metrics.MaintainabilityIndex,
                        Method               = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        FullName             = sym.ToDisplayString(),
                        Project              = project.Name,
                        document.FilePath,
                        Line                 = metrics.Line,
                        LinesOfCode          = metrics.LinesOfCode,
                        ParameterCount       = metrics.ParameterCount,
                    }));
                }
            }
        }

        var hotspots = results
            .OrderByDescending(r => r.Cognitive)
            .ThenByDescending(r => r.Cc)
            .Take(topN)
            .Select((r, i) =>
            {
                dynamic e = r.Entry;
                return (object)new
                {
                    Rank                 = i + 1,
                    e.RiskLabel,
                    e.CyclomaticComplexity,
                    e.CognitiveComplexity,
                    e.MaxNesting,
                    e.MaintainabilityIndex,
                    e.Method,
                    e.FullName,
                    e.Project,
                    e.FilePath,
                    e.Line,
                    e.LinesOfCode,
                    e.ParameterCount,
                };
            })
            .ToList();

        var distribution = new
        {
            Critical = results.Count(r => r.Cognitive >= 25 || r.Cc >= 25),
            High     = results.Count(r => (r.Cognitive is >= 15 and < 25) || (r.Cognitive < 25 && r.Cc is >= 15 and < 25)),
            Medium   = results.Count(r => r.Cognitive is >= 10 and < 15 && r.Cc < 15),
            Low      = results.Count(r => r.Cognitive is >= 5 and < 10 && r.Cc < 10),
        };

        return new
        {
            projectFilter   = projectName ?? "all",
            topN,
            minComplexity,
            totalAboveThreshold = results.Count,
            rankedBy = "cognitive complexity (the reader's view), then cyclomatic",
            distribution,
            hotspots,
        };
    }
}
