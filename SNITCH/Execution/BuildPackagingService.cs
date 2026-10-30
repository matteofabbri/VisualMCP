using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// The packaging side the product did not have: publish (with the common shapes), project
/// references, and NuGet pack/push — each one through the project's own CLI, answered as structured
/// data instead of a transcript.
/// </summary>
public interface IBuildPackagingService
{
    Task<object> PublishAsync(string? projectName, string configuration, string? runtime, bool selfContained,
        bool singleFile, string? output, bool noBuild, int timeoutSeconds);

    Task<object> AddProjectReferenceAsync(string projectName, string reference, int timeoutSeconds);
    Task<object> RemoveProjectReferenceAsync(string projectName, string reference, int timeoutSeconds);
    Task<object> PackAsync(string? projectName, string configuration, string? output, string? version, bool noBuild,
        int timeoutSeconds);
    Task<object> PushAsync(string packagePath, string? source, string? apiKey, int timeoutSeconds);
}

public sealed class BuildPackagingService : IBuildPackagingService
{
    private readonly IRoslynWorkspace _workspace;
    public BuildPackagingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public Task<object> PublishAsync(string? projectName = null, string configuration = "Release",
        string? runtime = null, bool selfContained = false, bool singleFile = false, string? output = null,
        bool noBuild = false, int timeoutSeconds = 300)
    {
        return RunAsync(projectName, project =>
        {
            var arguments = new List<string>
            {
                "publish", Quote(project.FilePath!),
                "-c", configuration,
            };

            if (!string.IsNullOrWhiteSpace(runtime))
            {
                arguments.Add("-r");
                arguments.Add(runtime!);
                arguments.Add(selfContained ? "--self-contained true" : "--self-contained false");
            }

            if (singleFile) arguments.Add("-p:PublishSingleFile=true");
            if (!string.IsNullOrWhiteSpace(output)) arguments.AddRange(["-o", Quote(output!)]);
            if (noBuild) arguments.Add("--no-build");

            return arguments;
        }, directory: null, timeoutSeconds, "published");
    }

    public Task<object> AddProjectReferenceAsync(string projectName, string reference, int timeoutSeconds = 120)
        => ReferenceAsync(projectName, reference, add: true, timeoutSeconds);

    public Task<object> RemoveProjectReferenceAsync(string projectName, string reference, int timeoutSeconds = 120)
        => ReferenceAsync(projectName, reference, add: false, timeoutSeconds);

    public Task<object> PackAsync(string? projectName = null, string configuration = "Release", string? output = null,
        string? version = null, bool noBuild = false, int timeoutSeconds = 300)
    {
        return RunAsync(projectName, project =>
        {
            var arguments = new List<string> { "pack", Quote(project.FilePath!), "-c", configuration };
            if (!string.IsNullOrWhiteSpace(output)) arguments.AddRange(["-o", Quote(output!)]);
            if (!string.IsNullOrWhiteSpace(version)) arguments.AddRange(["-p:PackageVersion=" + version]);
            if (noBuild) arguments.Add("--no-build");
            return arguments;
        }, directory: null, timeoutSeconds, "packed");
    }

    public async Task<object> PushAsync(string packagePath, string? source = null, string? apiKey = null,
        int timeoutSeconds = 180)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return new { error = "A package path is required." };

        var full = Path.GetFullPath(packagePath);
        if (!File.Exists(full)) return new { error = $"Package not found: {full}" };

        var arguments = new List<string> { "nuget", "push", Quote(full) };
        if (!string.IsNullOrWhiteSpace(source)) arguments.AddRange(["--source", source!]);
        if (!string.IsNullOrWhiteSpace(apiKey)) arguments.AddRange(["--api-key", apiKey!]);

        var directory = Path.GetDirectoryName(full)!;
        var timeout = Math.Clamp(timeoutSeconds, 5, 900);
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", string.Join(" ", arguments), directory, timeout);

        return new
        {
            action = "nuget push",
            package = full,
            source = source ?? "the configured default",
            apiKey = apiKey is null ? null : "(supplied, not echoed)",
            command = "dotnet " + string.Join(" ", arguments.Where(argument => argument != apiKey)),
            timedOut,
            exitCode = timedOut ? (int?)null : exitCode,
            succeeded = !timedOut && exitCode == 0,
            durationMs = (long)elapsed.TotalMilliseconds,
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
            note = "A push publishes the package to the feed: there is no preview.",
        };
    }

    // ── plumbing ────────────────────────────────────────────────────────────
    private async Task<object> ReferenceAsync(string projectName, string reference, bool add, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return new { error = "A project name is required." };
        if (string.IsNullOrWhiteSpace(reference)) return new { error = "A reference (project name or .csproj path) is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        var project = solution.Projects.FirstOrDefault(candidate =>
            candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project?.FilePath is null) return new { error = $"Project '{projectName}' not found in the solution." };

        var target = solution.Projects.FirstOrDefault(candidate =>
                         candidate.Name.Equals(reference, StringComparison.OrdinalIgnoreCase))?.FilePath
                     ?? (File.Exists(reference) ? Path.GetFullPath(reference) : null);

        if (target is null)
            return new
            {
                error = $"'{reference}' is neither a project of the solution nor an existing file.",
                projects = solution.Projects.Select(candidate => candidate.Name).ToList(),
            };

        var verb = add ? "add" : "remove";
        var arguments = $"reference {Quote(project.FilePath)} {verb} {Quote(target)}";
        var directory = Path.GetDirectoryName(project.FilePath)!;
        var timeout = Math.Clamp(timeoutSeconds, 5, 600);
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", arguments, directory, timeout);

        return new
        {
            action = $"project reference {verb}",
            project = project.Name,
            reference = Path.GetFileName(target),
            command = $"dotnet {arguments}",
            timedOut,
            exitCode = timedOut ? (int?)null : exitCode,
            succeeded = !timedOut && exitCode == 0,
            durationMs = (long)elapsed.TotalMilliseconds,
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
        };
    }

    private async Task<object> RunAsync(string? projectName, Func<Project, List<string>> arguments,
        string? directory, int timeoutSeconds, string verb)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null) return new { error = "No C# solution could be auto-located from the working directory." };

        Project? project;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            project = solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return new { error = $"Project '{projectName}' not found in the solution." };
        }
        else
        {
            var candidates = solution.Projects.Where(candidate => candidate.FilePath is not null).ToList();
            if (candidates.Count != 1)
                return new
                {
                    error = "Several projects; pass projectName.",
                    candidates = candidates.Select(candidate => candidate.Name).ToList(),
                };

            project = candidates[0];
        }

        if (project.FilePath is null) return new { error = $"Project '{project.Name}' has no file on disk." };

        var args = arguments(project);
        var workingDirectory = directory ?? Path.GetDirectoryName(project.FilePath)!;
        var timeout = Math.Clamp(timeoutSeconds, 5, 1800);
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", string.Join(" ", args), workingDirectory, timeout);

        return new
        {
            action = verb,
            project = project.Name,
            command = "dotnet " + string.Join(" ", args),
            workingDirectory,
            timedOut,
            exitCode = timedOut ? (int?)null : exitCode,
            succeeded = !timedOut && exitCode == 0,
            durationMs = (long)elapsed.TotalMilliseconds,
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
            note = exitCode == 0
                ? null
                : "A failing publish/pack is usually a missing workload (e.g. a RID-specific runtime pack) " +
                  "or a project that does not produce that artifact.",
        };
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
