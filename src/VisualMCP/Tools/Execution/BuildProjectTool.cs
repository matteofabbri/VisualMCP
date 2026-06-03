using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class BuildProjectTool
{
    // Matches MSBuild diagnostic lines:
    //   path(line,col): error|warning CODE: message [project]
    //   path : error|warning CODE: message [project]   (no position)
    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+?)(?:\((?<line>\d+),(?<col>\d+)\))?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Z]{1,3}\d+)\s*:\s*(?<msg>.+?)(?:\s*\[(?<proj>[^\]]+)\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    [McpServerTool, Description(
        "Runs 'dotnet build' on a project (or the whole solution) and returns a structured list of " +
        "compiler errors and warnings, plus the overall build result. " +
        "By default compiles without copying output files so it works even while the app is running " +
        "(avoids the DLL-in-use lock). Use this to get the real MSBuild verdict, which catches " +
        "source-generator and target-file issues that Roslyn's in-memory model may miss.")]
    public static async Task<object> BuildProject(
        [Description("Optional: name of the project to build. If omitted, the whole solution is built.")] string? projectName = null,
        [Description("Build configuration (e.g. 'Debug' or 'Release'). Default: Debug.")] string configuration = "Debug",
        [Description("Skip copying output files to bin/ — avoids file-in-use errors when the app is already running (default: true).")] bool noCopyOutput = true,
        [Description("Restore NuGet packages before building (default: true).")] bool restore = true,
        [Description("Build timeout in seconds (default: 120).")] int timeoutSeconds = 120)
    {
        // Resolve what to build: a specific project path, or the solution file.
        string buildTarget;
        string workingDir;

        var service  = RoslynWorkspaceService.Instance;
        var solution = await service.EnsureSolutionLoadedAsync();

        if (projectName is not null)
        {
            if (solution is null)
                return new { error = "No solution loaded. Call load_solution first, or omit projectName to build a solution file directly." };

            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null)
                return new { error = $"Project '{projectName}' not found in the solution." };
            if (project.FilePath is null)
                return new { error = $"Project '{projectName}' has no file path on disk." };

            buildTarget = $"\"{project.FilePath}\"";
            workingDir  = Path.GetDirectoryName(project.FilePath)!;
        }
        else if (solution is not null)
        {
            var slnPath = solution.FilePath;
            if (slnPath is null)
                return new { error = "Solution has no file path. Pass an explicit projectName." };

            buildTarget = $"\"{slnPath}\"";
            workingDir  = Path.GetDirectoryName(slnPath)!;
        }
        else
        {
            return new { error = "No solution loaded and no projectName given. Call load_solution first." };
        }

        // Compose dotnet build arguments.
        var parts = new List<string>
        {
            "build", buildTarget,
            "-c", configuration,
            "--verbosity", "normal",
        };

        if (!restore)
            parts.Add("--no-restore");

        if (noCopyOutput)
        {
            // Compile only — no DLL copy, no file-lock issues.
            parts.Add("/p:CopyBuildOutputToOutputDirectory=false");
            parts.Add("/p:CopyOutputSymbolsToOutputDirectory=false");
            parts.Add("/p:SkipCopyBuildProduct=true");
        }

        var argsStr = string.Join(" ", parts);

        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", argsStr, workingDir, timeoutSeconds);

        if (timedOut)
            return new { error = $"Build timed out after {timeoutSeconds}s.", command = $"dotnet {argsStr}" };

        // Merge stdout + stderr for parsing (MSBuild writes everything to stdout).
        var combined = stdout + "\n" + stderr;

        // Parse structured diagnostics.
        var errors   = new List<object>();
        var warnings = new List<object>();

        foreach (Match m in DiagnosticLine.Matches(combined))
        {
            var entry = new
            {
                file     = m.Groups["file"].Value.Trim(),
                line     = m.Groups["line"].Success  ? int.Parse(m.Groups["line"].Value)  : (int?)null,
                column   = m.Groups["col"].Success   ? int.Parse(m.Groups["col"].Value)   : (int?)null,
                code     = m.Groups["code"].Value.Trim().ToUpperInvariant(),
                message  = m.Groups["msg"].Value.Trim(),
                project  = m.Groups["proj"].Success  ? Path.GetFileNameWithoutExtension(m.Groups["proj"].Value) : null,
            };

            if (m.Groups["severity"].Value.Equals("error", StringComparison.OrdinalIgnoreCase))
                errors.Add(entry);
            else
                warnings.Add(entry);
        }

        var succeeded = exitCode == 0;

        return new
        {
            succeeded,
            exitCode,
            errorCount   = errors.Count,
            warningCount = warnings.Count,
            configuration,
            noCopyOutput,
            durationMs   = (long)elapsed.TotalMilliseconds,
            command      = $"dotnet {argsStr}",
            errors,
            warnings,
            // Raw output for cases where the regex didn't catch something.
            rawOutput    = errors.Count == 0 && !succeeded
                             ? ProcessRunner.Truncate(combined)   // include raw if build failed but we parsed nothing
                             : null,
        };
    }
}
