using Microsoft.CodeAnalysis;
using VisualMCP.Implementation.Execution;
using VisualMCP.Parsing;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Benchmarking;

internal static class RunBenchmarksImpl
{
    internal static async Task<object> RunAsync(string? projectName, string filter, string? extraArgs, bool noBuild, int timeoutSeconds)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        Project? target;
        if (projectName is not null)
        {
            target = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (target is null) return new { error = $"Project '{projectName}' not found in the solution." };
        }
        else
        {
            var benches = solution.Projects.Where(IsBenchmarkProject).Select(p => p.Name).ToList();
            if (benches.Count == 0)
                return new { error = "No BenchmarkDotNet project found (no project references 'BenchmarkDotNet'). Specify projectName explicitly." };
            if (benches.Count > 1)
                return new { error = $"Multiple benchmark projects found; specify projectName. Candidates: {string.Join(", ", benches)}" };
            target = solution.Projects.First(p => p.Name == benches[0]);
        }

        var projectPath = target.FilePath;
        if (projectPath is null || !File.Exists(projectPath))
            return new { error = $"Project file not found on disk for '{target.Name}'." };

        if (timeoutSeconds < 30) timeoutSeconds = 30;
        if (timeoutSeconds > 1800) timeoutSeconds = 1800;

        var parts = new List<string> { "run", "-c", "Release", "--project", $"\"{projectPath}\"" };
        if (noBuild) parts.Add("--no-build");
        parts.Add("--");
        parts.Add($"--filter \"{(string.IsNullOrWhiteSpace(filter) ? "*" : filter)}\"");
        if (!string.IsNullOrWhiteSpace(extraArgs)) parts.Add(extraArgs);
        var args = string.Join(" ", parts);

        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, timeoutSeconds);

        // The BenchmarkDotNet summary table is at the END of the output — keep the tail.
        return new
        {
            project = target.Name,
            command = $"dotnet {args}",
            timedOut,
            exitCode = timedOut ? (int?)null : exitCode,
            durationMs = (long)elapsed.TotalMilliseconds,
            note = timedOut
                ? $"Benchmarks still running after {timeoutSeconds}s and were stopped (benchmarks can take long — raise timeoutSeconds or narrow the filter)."
                : (exitCode == 0 ? "Benchmarks completed." : $"Process exited with code {exitCode}."),
            output = Tail(string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr, 20_000),
        };
    }

    private static bool IsBenchmarkProject(Project p)
    {
        if (p.FilePath is null || !File.Exists(p.FilePath)) return false;
        try
        {
            var info = CsprojParser.Parse(p.FilePath);
            return info.PackageReferences.Any(pr => pr.Name.Contains("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return p.Name.Contains("Benchmark", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Tail(string s, int max) =>
        s.Length <= max ? s : "…(head truncated)\n" + s[^max..];
}
