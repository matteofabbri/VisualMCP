using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool, Description("Return compiler errors and warnings for the loaded solution (or a single project). Equivalent to Visual Studio's Error List. Requires LoadSolution first.")]
    public static async Task<object> GetDiagnostics(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Minimum severity to include: Error, Warning, Info, Hidden (default: Warning)")] string minSeverity = "Warning")
    {
        var solution = RoslynWorkspaceService.Instance.CurrentSolution;
        if (solution is null)
            return new { error = "No solution loaded. Call load_solution first." };

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
                        Id         = d.Id,
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
