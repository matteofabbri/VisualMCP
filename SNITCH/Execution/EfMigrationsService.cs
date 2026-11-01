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
        string? startupProject, bool noBuild, int timeoutSeconds, string? fromMigration, string? toMigration);
}

public sealed class EfMigrationsService : IEfMigrationsService
{
    private static readonly string[] Actions =
        ["list", "add", "script", "diff", "remove", "update", "info", "dbcontext-list", "pending-model-changes"];

    private readonly IRoslynWorkspace _workspace;
    public EfMigrationsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> RunAsync(string? projectName = null, string action = "list", string? name = null,
        string? output = null, string? startupProject = null, bool noBuild = false, int timeoutSeconds = 120,
        string? fromMigration = null, string? toMigration = null)
    {
        var command = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (!Actions.Contains(command))
            return new { error = $"Unknown action '{action}'. Use one of: {string.Join(", ", Actions)}." };

        if (command == "diff" && string.IsNullOrWhiteSpace(fromMigration))
            return new
            {
                error = "The diff action needs fromMigration (use '0' for an empty database); toMigration is " +
                        "optional and defaults to the latest migration.",
            };

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
        var args = CommandArguments(command, name, output, fromMigration, toMigration);

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

        bool? pendingModelChanges = command != "pending-model-changes" || timedOut
            ? null
            : exitCode switch { 0 => false, 1 => true, _ => null };

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
            pendingModelChanges,
            stdout = ProcessRunner.Truncate(stdout),
            stderr = ProcessRunner.Truncate(stderr),
            hint = command switch
            {
                "pending-model-changes" =>
                    "Exit code 0: the model and the migrations agree. 1: the model has changed since the last " +
                    "migration, so a migration is pending. Any other code is a tool or project error.",
                "diff" =>
                    "The SQL between fromMigration and toMigration: that is the schema delta the database would " +
                    "receive. A field-level comparison of the model itself is not made here — the C# of the " +
                    "migration and the snapshot is reviewed with the normal tools.",
                _ when exitCode != 0 =>
                    "A failing 'dotnet ef' usually means the 'dotnet-ef' tool is missing (dotnet tool install --global dotnet-ef) " +
                    "or the project does not reference Microsoft.EntityFrameworkCore.Design.",
                _ =>
                    "A migration file is C#: review it with the normal C# tools; nothing here proves the model and the " +
                    "migrations agree, which is what a script or a diff does.",
            },
        };
    }

    /// <summary>The 'dotnet ef' arguments before the common project options.</summary>
    public static List<string> CommandArguments(string action, string? name, string? output,
        string? fromMigration = null, string? toMigration = null)
    {
        if (action == "info") return ["ef", "dbcontext", "info"];
        if (action == "dbcontext-list") return ["ef", "dbcontext", "list"];

        // 'diff' is 'script from to': the SQL between two migrations, which is the schema delta.
        var args = new List<string> { "ef", "migrations" };
        args.Add(action switch
        {
            "pending-model-changes" => "has-pending-model-changes",
            "diff" => "script",
            _ => action,
        });

        switch (action)
        {
            case "add" when !string.IsNullOrWhiteSpace(name): args.Add(Quote(name)); break;
            case "update" when !string.IsNullOrWhiteSpace(name): args.Add(Quote(name)); break;
            case "diff" when !string.IsNullOrWhiteSpace(fromMigration):
                args.Add(Quote(fromMigration));
                if (!string.IsNullOrWhiteSpace(toMigration)) args.Add(Quote(toMigration));
                break;
            case "script" when !string.IsNullOrWhiteSpace(output): args.AddRange(["--output", Quote(output)]); break;
        }

        if (action == "diff" && !string.IsNullOrWhiteSpace(output))
            args.AddRange(["--output", Quote(output)]);

        return args;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
