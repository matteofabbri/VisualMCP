using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

public interface IGetDiagnosticsService
{
    Task<object> GetDiagnostics(string? projectName, string minSeverity);
}

public sealed class GetDiagnosticsService : IGetDiagnosticsService
{
    private readonly IRoslynWorkspace _workspace;
    public GetDiagnosticsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> GetDiagnostics(
        string? projectName = null,
        string minSeverity = "Warning")
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx." };

        var threshold = minSeverity.ToLowerInvariant() switch
        {
            "hidden"  => DiagnosticSeverity.Hidden,
            "info"    => DiagnosticSeverity.Info,
            "warning" => DiagnosticSeverity.Warning,
            "error"   => DiagnosticSeverity.Error,
            _         => DiagnosticSeverity.Warning,
        };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var allDiagnostics = new List<object>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            var diags = compilation.GetDiagnostics()
                .Where(d => d.Severity >= threshold && !d.IsSuppressed)
                .Select(d =>
                {
                    var loc = d.Location.IsInSource ? d.Location.GetLineSpan() : default;
                    return new
                    {
                        Project    = project.Name,
                        Severity   = d.Severity.ToString(),
                        d.Id,
                        Message    = d.GetMessage(),
                        FilePath   = d.Location.IsInSource ? loc.Path : null,
                        Line       = d.Location.IsInSource ? loc.StartLinePosition.Line + 1 : (int?)null,
                        Column     = d.Location.IsInSource ? loc.StartLinePosition.Character + 1 : (int?)null,
                    };
                })
                .OrderBy(d => d.Severity == "Error" ? 0 : d.Severity == "Warning" ? 1 : 2)
                .ThenBy(d => d.FilePath)
                .ThenBy(d => d.Line)
                .ToList();

            allDiagnostics.AddRange(diags.Cast<object>());
        }

        var errors   = allDiagnostics.Count(d => ((dynamic)d).Severity == "Error");
        var warnings = allDiagnostics.Count(d => ((dynamic)d).Severity == "Warning");

        return new
        {
            projectFilter = projectName ?? "all",
            minSeverity,
            summary = new { errors, warnings, total = allDiagnostics.Count },
            diagnostics = allDiagnostics,
        };
    }
}
