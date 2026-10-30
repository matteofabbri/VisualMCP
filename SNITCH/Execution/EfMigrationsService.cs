using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// The EF Core migration workflow, which the C# model alone cannot give: list, add, script, remove
/// and apply migrations through the project's own <c>dotnet ef</c> tool, with a structured result
/// instead of a shell transcript.
/// </summary>
public interface IEfMigrationsService
{
    Task<object> RunAsync(string? projectName, string action, string? name, string? output,
        string? startupProject, bool noBuild, int timeoutSeconds);
}

public sealed class EfMigrationsService : IEfMigrationsService
{
    private static readonly string[] Actions =
        ["list", "add", "script", "remove", "update", "info", "dbcontext-list"];

    private readonly IRoslynWorkspace _workspace;
    public EfMigrationsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RunAsync(string? projectName = null, string action = "list", string? name = null,
        string? output = null, string? startupProject = null, bool noBuild = false, int timeoutSeconds = 120)
    {
        var command = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!Actions.Contains(command))
            return new { error = $"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}." };

        if (command == "add" && string.IsNullOrWhiteSpace(name))
            return new { error = "The add action needs a migration name." };

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory." };

        Project? project;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            project = solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project is null) return new { error = $"Project '{projectName}' not found in the solution." };
        }
        else
        {
            var candidates = solution.Projects
                .Where(candidate => string.Equals(Path.GetExtension(candidate.FilePath), ".csproj",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 1) project = candidates[0];
            else
                return new
                {
                    error = "Several projects could be the migrations project; pass projectName.",
                    candidates = candidates.Select(candidate => candidate.Name).ToList(),
                };
        }

        if (project.FilePath is null) return new { error = $"Project '{project.Name}' has no file on disk." };

        var directory = Path.GetDirectoryName(project.FilePath)!;
        var args = new List<string> { "ef", "migrations" };
        if (command == "info") { args = ["ef", "dbcontext", "info"]; }
        else if (command == "dbcontext-list") { args = ["ef", "dbcontext", "list"]; }
        else { args.Add(command == "list" ? "list" : command); }

        switch (command)
        {
            case "add": args.Add(Quote(name!)); break;
            case "update" when !string.IsNullOrWhiteSpace(name): args.Add(Quote(name)); break;
            case "script" when !string.IsNullOrWhiteSpace(output): args.AddRange(["--output", Quote(output)]); break;
        }

        args.AddRange(["--project", Quote(project.FilePath)]);
        if (!string.IsNullOrWhiteSpace(startupProject))
        {
            var startup = solution.Projects.FirstOrDefault(candidate =>
                candidate.Name.Equals(startupProject, StringComparison.OrdinalIgnoreCase));
            if (startup?.FilePath is null)
                return new { error = $"Startup project '{startupProject}' not found in the solution." };
            args.AddRange(["--startup-project", Quote(startup.FilePath)]);
        }
        if (noBuild) args.Add("--no-build");

        var commandLine = "dotnet " + string.Join(" ", args);
        var timeout = Math.Clamp(timeoutSeconds, 5, 900);
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ProcessRunner.RunAsync("dotnet", string.Join(" ", args), directory, timeout);

        return new
        {
            project = project.Name,
            projectPath = project.FilePath,
            workingDirectory = directory,
            action = command,
            command = commandLine,
            timedOut,
            exitCode = timedOut ? (int?)null : exitCode,
            durationMs = (long)elapsed.TotalMilliseconds,
            succeeded = !timedOut && exitCode == 0,
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
            hint = exitCode != 0
                ? "A failing 'dotnet ef' usually means the 'dotnet-ef' tool is missing (dotnet tool install --global dotnet-ef) " +
                  "or the project does not reference Microsoft.EntityFrameworkCore.Design."
                : "A migration file is C#: review it with the normal C# tools; nothing here proves the model and the " +
                  "migrations agree, which is what a script does.",
        };
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
