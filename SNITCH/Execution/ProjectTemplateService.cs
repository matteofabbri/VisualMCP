using System.Text;
using System.Text.RegularExpressions;

namespace Snitch.Execution;

/// <summary>One template <c>dotnet new list</c> reports.</summary>
public sealed record ProjectTemplate(string Name, string ShortName, string Languages, string Tags);

/// <summary>
/// The answer of <c>project_template</c>. Every member is nullable so a refusal leaves the rest unset
/// and the MCP serializer drops them.
/// </summary>
public sealed record ProjectTemplateResult
{
    /// <summary><c>list</c> or <c>create</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The templates installed for this SDK.</summary>
    public IReadOnlyList<ProjectTemplate>? Templates { get; init; }

    /// <summary>The command that ran (or would run, on a dry run).</summary>
    public string? Command { get; init; }

    /// <summary>The exit code of the command.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The command's own output.</summary>
    public string? Output { get; init; }

    /// <summary>The files the template created, relative to the output folder.</summary>
    public IReadOnlyList<string>? CreatedFiles { get; init; }

    /// <summary>Whether the call only reported what it would do.</summary>
    public bool? DryRun { get; init; }

    /// <summary>Whether the command succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Project templates: <c>dotnet new</c> as data. The gap this closes is the one the build section
/// carries — this server could create a file inside an existing project but not a project at all.
/// </summary>
public interface IProjectTemplateService
{
    Task<ProjectTemplateResult> RunAsync(
        string action,
        string? template,
        string? name,
        string? output,
        string? framework,
        string? language,
        bool dryRun,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectTemplateService : IProjectTemplateService
{
    private static readonly Regex ColumnSplit = new(@"\s{2,}", RegexOptions.CultureInvariant);

    public async Task<ProjectTemplateResult> RunAsync(
        string action,
        string? template,
        string? name,
        string? output,
        string? framework,
        string? language,
        bool dryRun,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();

        return verb switch
        {
            "list" => await ListAsync(timeoutSeconds, cancellationToken),
            "create" => await CreateAsync(template, name, output, framework, language, dryRun, timeoutSeconds, cancellationToken),
            _ => new ProjectTemplateResult { Error = $"Unknown action '{action}': use list or create." },
        };
    }

    private static async Task<ProjectTemplateResult> ListAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
            "dotnet", "new list", Directory.GetCurrentDirectory(), timeoutSeconds);

        var templates = ParseList(stdout);

        return new ProjectTemplateResult
        {
            Action = "list",
            Command = "dotnet new list",
            ExitCode = exitCode,
            Output = Truncate(stdout),
            Templates = templates,
            Succeeded = !timedOut && exitCode == 0,
            Note = timedOut
                ? "dotnet new list timed out."
                : templates.Count == 0
                    ? "No template parsed: the SDK's output did not have the table shape; read Output."
                    : $"{templates.Count} template(s); create one with action=create template=<shortName>.",
            Error = exitCode != 0 && !string.IsNullOrWhiteSpace(stderr) ? Truncate(stderr) : null,
        };
    }

    private async Task<ProjectTemplateResult> CreateAsync(
        string? template,
        string? name,
        string? output,
        string? framework,
        string? language,
        bool dryRun,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var shortName = (template ?? string.Empty).Trim();
        if (shortName.Length == 0)
            return new ProjectTemplateResult { Action = "create", Error = "A template short name is required (see action=list)." };

        var projectName = (name ?? string.Empty).Trim();
        if (projectName.Length == 0)
            return new ProjectTemplateResult { Action = "create", Error = "A project name is required." };

        var directory = string.IsNullOrWhiteSpace(output)
            ? Path.GetFullPath(projectName, Directory.GetCurrentDirectory())
            : Path.GetFullPath(output);

        var command = new StringBuilder("dotnet new ").Append(Quote(shortName))
            .Append(" --name ").Append(Quote(projectName))
            .Append(" --output ").Append(Quote(directory));

        if (!string.IsNullOrWhiteSpace(framework)) command.Append(" --framework ").Append(Quote(framework));
        if (!string.IsNullOrWhiteSpace(language)) command.Append(" --language ").Append(Quote(language));

        var invocation = command.ToString();
        if (dryRun)
        {
            return new ProjectTemplateResult
            {
                Action = "create",
                Command = invocation,
                DryRun = true,
                Succeeded = false,
                Note = "Preview only; pass dryRun=false to create the project.",
            };
        }

        var existed = Directory.Exists(directory);
        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
            "dotnet", invocation["dotnet ".Length..], Directory.GetCurrentDirectory(), timeoutSeconds);

        var created = Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, directory, StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(directory, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToList()
            : [];

        var succeeded = !timedOut && exitCode == 0;

        return new ProjectTemplateResult
        {
            Action = "create",
            Command = invocation,
            ExitCode = exitCode,
            Output = Truncate(stdout),
            CreatedFiles = created,
            DryRun = false,
            Succeeded = succeeded,
            Note = succeeded
                ? $"{(existed ? "The template was applied into the existing folder" : "The project was created")} at {directory}; " +
                  $"add it to a solution with add_projects_to_solution."
                : null,
            Error = succeeded
                ? null
                : timedOut
                    ? "dotnet new timed out."
                    : string.IsNullOrWhiteSpace(stderr) ? stdout : Truncate(stderr),
        };
    }

    /// <summary>Reads the table <c>dotnet new list</c> prints, after its dashes line.</summary>
    private static List<ProjectTemplate> ParseList(string output)
    {
        var templates = new List<ProjectTemplate>();
        var inTable = false;

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0) continue;

            if (!inTable)
            {
                // The dashes line is the one place the header ends in every locale.
                if (trimmed.Trim('-', ' ').Length == 0 && trimmed.Contains('-')) inTable = true;
                continue;
            }

            var columns = ColumnSplit.Split(trimmed).Where(column => column.Length > 0).ToList();
            if (columns.Count < 3) continue;

            var tags = columns.Count > 3 ? string.Join(", ", columns.Skip(3)) : string.Empty;
            templates.Add(new ProjectTemplate(columns[0].Trim(), columns[1].Trim(), columns[2].Trim(), tags));
        }

        return templates;
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static string Truncate(string value, int limit = 8000) =>
        value.Length <= limit ? value : value[..limit] + $"…(truncated, {value.Length - limit} more chars)";
}
