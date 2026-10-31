using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Execution;

public interface IRunProjectService
{
    Task<object> RunAsync(
        string? projectName, string? appArgs, int timeoutSeconds, string? configuration, bool noBuild,
        string? configName = null);
}

public sealed class RunProjectService : IRunProjectService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IRunConfigurationService? _configurations;

    public RunProjectService(IRoslynWorkspace workspace, IRunConfigurationService? configurations = null)
    {
        _workspace = workspace;
        _configurations = configurations;
    }

    public async Task<object> RunAsync(
        string? projectName, string? appArgs, int timeoutSeconds, string? configuration, bool noBuild,
        string? configName = null)
    {
        RunConfiguration? config = null;
        if (!string.IsNullOrWhiteSpace(configName))
        {
            if (_configurations is null)
                return new { error = "Run configurations are not available in this host." };

            var (resolved, configError) = await _configurations.ResolveAsync(configName.Trim());
            if (configError is not null) return configError;
            config = resolved;
        }

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx." };

        // The configuration is a set of defaults; what the caller passes explicitly wins.
        projectName ??= config?.Project;
        appArgs ??= config?.Args;
        configuration ??= config?.Configuration;
        noBuild |= config?.NoBuild ?? false;

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

        var environment = config?.Environment is { Count: > 0 } fromConfig
            ? new Dictionary<string, string>(fromConfig, StringComparer.OrdinalIgnoreCase)
            : null;

        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", args, Path.GetDirectoryName(projectPath)!, timeoutSeconds, environment);

        return new
        {
            project   = target.Name,
            projectPath,
            config    = config?.Name,
            configuration,
            noBuild,
            environment = environment is null ? null : environment.Keys.OrderBy(name => name).ToList(),
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
