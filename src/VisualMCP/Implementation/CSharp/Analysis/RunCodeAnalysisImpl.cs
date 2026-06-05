using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.CSharp.Analysis;

internal static class RunCodeAnalysisImpl
{
    internal static async Task<object> RunAsync(string? projectName, string minSeverity, int maxResults, int timeoutSeconds)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var min = ParseSeverity(minSeverity);
        if (timeoutSeconds < 5) timeoutSeconds = 5;
        if (timeoutSeconds > 600) timeoutSeconds = 600;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        var findings = new List<object>();
        var projectsWithoutAnalyzers = new List<string>();
        var truncated = false;

        try
        {
            foreach (var project in projects)
            {
                var analyzers = project.AnalyzerReferences
                    .SelectMany(r => SafeGetAnalyzers(r, project.Language))
                    .ToImmutableArray();

                if (analyzers.IsEmpty) { projectsWithoutAnalyzers.Add(project.Name); continue; }

                var compilation = await project.GetCompilationAsync(cts.Token);
                if (compilation is null) continue;

                var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
                var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cts.Token);

                foreach (var d in diags)
                {
                    if (d.Severity < min) continue;
                    if (findings.Count >= maxResults) { truncated = true; break; }

                    var loc = d.Location.IsInSource ? d.Location.GetLineSpan() : default;
                    findings.Add(new
                    {
                        Id       = d.Id,
                        Severity = d.Severity.ToString(),
                        Category = d.Descriptor.Category,
                        Message  = d.GetMessage(),
                        Project  = project.Name,
                        FilePath = d.Location.IsInSource ? loc.Path : null,
                        Line     = d.Location.IsInSource ? loc.StartLinePosition.Line + 1 : (int?)null,
                    });
                }
                if (truncated) break;
            }
        }
        catch (OperationCanceledException)
        {
            return new { error = $"Code analysis timed out after {timeoutSeconds}s. Restrict to a single project or raise timeoutSeconds.", partialFindings = findings.Count };
        }

        var bySeverity = findings.GroupBy(f => ((dynamic)f).Severity.ToString()).ToDictionary(g => g.Key, g => g.Count());
        var byId       = findings.GroupBy(f => ((dynamic)f).Id.ToString()).ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            projectFilter = projectName ?? "all",
            minSeverity = min.ToString(),
            totalFindings = findings.Count,
            truncated,
            projectsWithoutAnalyzers,
            bySeverity,
            byId,
            findings,
        };
    }

    private static ImmutableArray<DiagnosticAnalyzer> SafeGetAnalyzers(AnalyzerReference r, string language)
    {
        try { return r.GetAnalyzers(language); }
        catch { return ImmutableArray<DiagnosticAnalyzer>.Empty; }
    }

    private static DiagnosticSeverity ParseSeverity(string s) => s?.Trim().ToLowerInvariant() switch
    {
        "error"   => DiagnosticSeverity.Error,
        "info"    => DiagnosticSeverity.Info,
        "hidden"  => DiagnosticSeverity.Hidden,
        _          => DiagnosticSeverity.Warning,
    };
}
