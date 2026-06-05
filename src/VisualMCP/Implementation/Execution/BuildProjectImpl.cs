using System.Text.RegularExpressions;
using VisualMCP.Implementation.Analysis;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Execution;

internal static class BuildProjectImpl
{
    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+?)(?:\((?<line>\d+),(?<col>\d+)\))?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Z]{1,3}\d+)\s*:\s*(?<msg>.+?)(?:\s*\[(?<proj>[^\]]+)\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    internal static async Task<object> RunAsync(string? projectName, string configuration, bool noCopyOutput, bool restore, int timeoutSeconds, bool runAnalyzers = true)
    {
        string buildTarget;
        string workingDir;

        var service  = RoslynWorkspaceService.Instance;
        var solution = await service.EnsureSolutionLoadedAsync();

        if (projectName is not null)
        {
            if (solution is null)
                return new { error = "No solution loaded. Call load_solution first, or omit projectName to build a solution file directly." };

            var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return new { error = $"Project '{projectName}' not found in the solution." };
            if (project.FilePath is null) return new { error = $"Project '{projectName}' has no file path on disk." };

            buildTarget = $"\"{project.FilePath}\"";
            workingDir  = Path.GetDirectoryName(project.FilePath)!;
        }
        else if (solution is not null)
        {
            var slnPath = solution.FilePath;
            if (slnPath is null) return new { error = "Solution has no file path. Pass an explicit projectName." };
            buildTarget = $"\"{slnPath}\"";
            workingDir  = Path.GetDirectoryName(slnPath)!;
        }
        else
        {
            return new { error = "No solution loaded and no projectName given. Call load_solution first." };
        }

        var parts = new List<string> { "build", buildTarget, "-c", configuration, "--verbosity", "normal" };
        if (!restore) parts.Add("--no-restore");
        if (noCopyOutput)
        {
            parts.Add("/p:CopyBuildOutputToOutputDirectory=false");
            parts.Add("/p:CopyOutputSymbolsToOutputDirectory=false");
            parts.Add("/p:SkipCopyBuildProduct=true");
        }

        var argsStr = string.Join(" ", parts);
        var (exitCode, timedOut, stdout, stderr, elapsed) = await ProcessRunner.RunAsync("dotnet", argsStr, workingDir, timeoutSeconds);

        if (timedOut)
            return new { error = $"Build timed out after {timeoutSeconds}s.", command = $"dotnet {argsStr}" };

        var combined = stdout + "\n" + stderr;
        var errors   = new List<object>();
        var warnings = new List<object>();

        foreach (Match m in DiagnosticLine.Matches(combined))
        {
            var entry = new
            {
                file     = m.Groups["file"].Value.Trim(),
                line     = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : (int?)null,
                column   = m.Groups["col"].Success  ? int.Parse(m.Groups["col"].Value)  : (int?)null,
                code     = m.Groups["code"].Value.Trim().ToUpperInvariant(),
                message  = m.Groups["msg"].Value.Trim(),
                project  = m.Groups["proj"].Success ? Path.GetFileNameWithoutExtension(m.Groups["proj"].Value) : null,
            };
            if (m.Groups["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)) errors.Add(entry);
            else warnings.Add(entry);
        }

        var succeeded = exitCode == 0;

        // Automatically run the configured Roslyn analyzers so a build also flags
        // analyzer/code-style issues (not just compiler diagnostics).
        object? analysis = null;
        if (runAnalyzers)
        {
            try { analysis = await RunCodeAnalysisImpl.RunAsync(projectName, "Warning", 200, 120); }
            catch (Exception ex) { analysis = new { error = $"Analyzer pass failed: {ex.Message}" }; }
        }

        return new
        {
            succeeded, exitCode,
            errorCount = errors.Count, warningCount = warnings.Count,
            configuration, noCopyOutput,
            durationMs = (long)elapsed.TotalMilliseconds,
            command = $"dotnet {argsStr}",
            errors, warnings,
            analysis,
            rawOutput = errors.Count == 0 && !succeeded ? ProcessRunner.Truncate(combined) : null,
        };
    }
}
