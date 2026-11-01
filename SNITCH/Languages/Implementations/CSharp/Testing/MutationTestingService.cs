using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Snitch.Execution;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>
/// Runs Stryker.NET over a test project and reports what it found: the mutation score, how many mutants
/// were killed, survived, timed out or were never covered, and the files that survived most. The tool
/// wraps the external runner (like <c>ef_migrations</c> wraps <c>dotnet ef</c>); the report is parsed
/// instead of handing back the console text, so the answer survives a locale and a long run.
/// </summary>
public interface IMutationTestingService
{
    Task<object> RunAsync(string? projectName, int timeoutSeconds, string? extraArgs);
}

public sealed class MutationTestingService : IMutationTestingService
{
    private readonly IRoslynWorkspace _workspace;
    public MutationTestingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RunAsync(string? projectName, int timeoutSeconds, string? extraArgs)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var project = ResolveTestProject(solution, projectName, out var resolveError);
        if (project is null) return new { error = resolveError };

        var directory = Path.GetDirectoryName(project.FilePath!)!;
        var output = Path.Combine(Path.GetTempPath(), $"vmcp-stryker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);

        try
        {
            var arguments = $"--reporter json --reporter progress --output \"{output.Replace('\\', '/')}\"";
            if (!string.IsNullOrWhiteSpace(extraArgs)) arguments += " " + extraArgs.Trim();

            var timeout = Math.Clamp(timeoutSeconds < 1 ? 1800 : timeoutSeconds, 60, 3600);
            var (exitCode, timedOut, stdout, stderr, elapsed) =
                await ProcessRunner.RunAsync("dotnet", "stryker " + arguments, directory, timeout);

            var reportPath = Directory.Exists(output)
                ? Directory.GetFiles(output, "mutation-report.json", SearchOption.AllDirectories).FirstOrDefault()
                : null;

            if (timedOut)
                return new
                {
                    error = $"Stryker did not finish within {timeout}s and was stopped.",
                    project = project.Name,
                    output = ProcessRunner.Truncate((stdout + "\n" + stderr).Trim(), 4000),
                };

            if (reportPath is null)
            {
                var combined = (stdout + "\n" + stderr).Trim();
                var missingTool = combined.Contains("No executable found matching command", StringComparison.OrdinalIgnoreCase) ||
                                  combined.Contains("is not recognized", StringComparison.OrdinalIgnoreCase);

                return new
                {
                    error = missingTool
                        ? "Stryker.NET is not installed or could not run."
                        : $"Stryker exited with code {exitCode} without writing a report.",
                    hint = missingTool
                        ? "Install it once with 'dotnet tool install -g dotnet-stryker' (or as a local tool: 'dotnet new tool-manifest' then 'dotnet tool install dotnet-stryker')."
                        : null,
                    project = project.Name,
                    command = $"dotnet stryker {arguments}",
                    output = ProcessRunner.Truncate(combined, 6000),
                };
            }

            var json = await File.ReadAllTextAsync(reportPath);
            var summary = Parse(json, project.Name);
            return summary;
        }
        finally
        {
            try { if (Directory.Exists(output)) Directory.Delete(output, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Parses Stryker's mutation-report.json; public so the report shape is testable offline.</summary>
    public static object Parse(string json, string projectName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var killed = 0;
        var survived = 0;
        var timedOutCount = 0;
        var noCoverage = 0;
        var ignored = 0;
        var compileErrors = 0;
        var runtimeErrors = 0;

        var perFile = new List<(string File, int Total, int Killed, int Survived, int NoCoverage)>();
        var survivedMutants = new List<object>();

        if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in files.EnumerateObject())
            {
                var fileTotal = 0;
                var fileKilled = 0;
                var fileSurvived = 0;
                var fileNoCoverage = 0;

                if (!file.Value.TryGetProperty("mutants", out var mutants) || mutants.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var mutant in mutants.EnumerateArray())
                {
                    var status = Str(mutant, "status");
                    var mutator = Str(mutant, "mutatorName");
                    var replacement = Str(mutant, "replacement");
                    var line = LineOf(mutant);

                    fileTotal++;
                    switch (status)
                    {
                        case "Killed": killed++; fileKilled++; break;
                        case "Survived": survived++; fileSurvived++; break;
                        case "NoCoverage": noCoverage++; fileNoCoverage++; break;
                        case "Timeout": timedOutCount++; break;
                        case "Ignored": ignored++; break;
                        case "CompileError": compileErrors++; break;
                        case "RuntimeError": runtimeErrors++; break;
                    }

                    if (status is "Survived" or "NoCoverage" && survivedMutants.Count < 100)
                        survivedMutants.Add(new { file = file.Name, line, mutator, replacement, status });
                }

                if (fileTotal > 0)
                    perFile.Add((file.Name, fileTotal, fileKilled, fileSurvived, fileNoCoverage));
            }
        }

        var tested = killed + timedOutCount + runtimeErrors + survived + noCoverage;
        var detected = killed + timedOutCount + runtimeErrors;
        var score = tested == 0 ? 0d : Math.Round(100d * detected / tested, 2);

        var thresholds = new { high = 80, low = 60, breakAt = 0 };
        if (root.TryGetProperty("thresholds", out var thresholdsElement) && thresholdsElement.ValueKind == JsonValueKind.Object)
        {
            thresholds = new
            {
                high = Int(thresholdsElement, "high", 80),
                low = Int(thresholdsElement, "low", 60),
                breakAt = Int(thresholdsElement, "break", 0),
            };
        }

        return new
        {
            project = projectName,
            schemaVersion = Str(root, "schemaVersion"),
            mutationScore = score,
            thresholds,
            totals = new
            {
                mutants = killed + survived + timedOutCount + noCoverage + ignored + compileErrors + runtimeErrors,
                killed,
                survived,
                timedOut = timedOutCount,
                noCoverage,
                ignored,
                compileErrors,
                runtimeErrors,
            },
            filesWithMutants = perFile.Count,
            worstFiles = perFile
                .OrderByDescending(file => file.Survived)
                .ThenByDescending(file => file.Total)
                .Take(10)
                .Select(file => new
                {
                    file = file.File,
                    mutants = file.Total,
                    killed = file.Killed,
                    survived = file.Survived,
                    noCoverage = file.NoCoverage,
                    score = file.Total == 0 ? 0d : Math.Round(100d * file.Killed / Math.Max(1, file.Total - file.NoCoverage), 2),
                })
                .ToList(),
            survivedMutants = survivedMutants.Take(25).ToList(),
            note = tested == 0
                ? "No mutant was generated: check that the project under test has no [ExcludeFromCodeCoverage] and that Stryker produced mutants for it."
                : "Score = detected / tested, where detected is Killed + Timeout + RuntimeError; Ignored and CompileError are excluded. A survivor is a mutation the suite did not notice.",
        };
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static int? LineOf(JsonElement mutant)
    {
        if (!mutant.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object)
            return null;
        if (!location.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object)
            return null;
        return start.TryGetProperty("line", out var line) && line.TryGetInt32(out var number) ? number : null;
    }

    /// <summary>The test project to mutate: the named one, or the single project that looks like a test project.</summary>
    private static Project? ResolveTestProject(Solution solution, string? projectName, out string? error)
    {
        error = null;
        var projects = solution.Projects.Where(project => project.FilePath is not null).ToList();

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var named = projects.FirstOrDefault(project =>
                string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;

            var byPath = projects.FirstOrDefault(project =>
                string.Equals(Path.GetFullPath(project.FilePath!), Path.GetFullPath(projectName!), StringComparison.OrdinalIgnoreCase));
            if (byPath is not null) return byPath;

            error = $"Project '{projectName}' not found in the loaded solution.";
            return null;
        }

        var candidates = projects
            .Where(project => project.Name.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                              (project.AssemblyName ?? string.Empty).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 1) return candidates[0];

        error = candidates.Count == 0
            ? "No test project found in the solution: pass projectName (the project whose tests run the mutation)."
            : $"{candidates.Count} test projects found: {string.Join(", ", candidates.Select(project => project.Name))}. Pass projectName.";
        return null;
    }
}
