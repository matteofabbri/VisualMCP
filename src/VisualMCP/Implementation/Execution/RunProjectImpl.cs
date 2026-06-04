using Microsoft.CodeAnalysis;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Execution;

internal static class RunProjectImpl
{
    internal static async Task<object> RunAsync(string? projectName, string? appArgs, int timeoutSeconds, string? configuration, bool noBuild)
    {
        var service  = RoslynWorkspaceService.Instance;
        var solution = await service.EnsureSolutionLoadedAsync();
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
            var runnable = solution.Projects.Where(IsRunnable).Select(p => p.Name).ToList();
            if (runnable.Count == 0) return new { error = "No runnable (executable) project found in the solution. Specify projectName explicitly." };
            if (runnable.Count > 1) return new { error = $"Multiple runnable projects found; specify projectName. Candidates: {string.Join(", ", runnable)}" };
            target = solution.Projects.First(p => p.Name == runnable[0]);
        }

        var projectPath = target.FilePath;
        if (projectPath is null || !File.Exists(projectPath))
            return new { error = $"Project file not found on disk for '{target.Name}'." };

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 300) timeoutSeconds = 300;

        var parts = new List<string> { "run", "--project", $"\"{projectPath}\"" };
        if (configuration is not null) { parts.Add("-c"); parts.Add(configuration); }
        if (noBuild) parts.Add("--no-build");
        if (!string.IsNullOrWhiteSpace(appArgs)) { parts.Add("--"); parts.Add(appArgs); }
        var args = string.Join(" ", parts);

        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, timeoutSeconds);

        return new
        {
            project   = target.Name,
            projectPath,
            command   = $"dotnet {args}",
            timedOut,
            exitCode  = timedOut ? (int?)null : exitCode,
            durationMs = (long)elapsed.TotalMilliseconds,
            note = timedOut
                ? $"App was still running after {timeoutSeconds}s and was stopped. Output below is what it produced so far."
                : (exitCode == 0 ? "App exited normally." : $"App exited with code {exitCode}."),
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
        };
    }

    private static bool IsRunnable(Project p) =>
        p.CompilationOptions?.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;
}
