using System.Text.Json;
using System.Text.Json.Serialization;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>A saved way to launch one project: the arguments, the environment and the configuration.</summary>
public sealed record RunConfiguration(
    string Name,
    string? Project,
    string? Args,
    string? Configuration,
    bool NoBuild,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Run configurations, saved per solution in <c>.visualMCP/run-configs.json</c>: the arguments and
/// environment variables an app needs are written once and reused, instead of being retyped on every
/// <c>run_project</c> call. The file is the source of truth and can be edited by hand or committed.
/// </summary>
public interface IRunConfigurationService
{
    Task<object> ListAsync();
    Task<object> SaveAsync(
        string name, string? project, string? args, string? configuration, bool noBuild,
        Dictionary<string, string>? environment);
    Task<object> DeleteAsync(string name);
    Task<(RunConfiguration? Configuration, object? Error)> ResolveAsync(string name);
}

public sealed class RunConfigurationService : IRunConfigurationService
{
    private const string FolderName = ".visualMCP";
    private const string FileName = "run-configs.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRoslynWorkspace _workspace;

    public RunConfigurationService(IRoslynWorkspace workspace) => _workspace = workspace;

    // ── list ──────────────────────────────────────────────────────────────────
    public async Task<object> ListAsync()
    {
        var (path, store) = await ReadAsync();
        if (store is null)
            return new
            {
                configsPath = path,
                exists = false,
                configs = Array.Empty<object>(),
                note = "No run configuration has been saved for this solution yet. Save one with action=save.",
            };

        return new
        {
            configsPath = path,
            exists = true,
            configs = store.Configs
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => (object)new
                {
                    name = pair.Key,
                    project = pair.Value.Project,
                    args = pair.Value.Args,
                    configuration = pair.Value.Configuration,
                    noBuild = pair.Value.NoBuild,
                    environment = pair.Value.Environment,
                })
                .ToList(),
        };
    }

    // ── save (upsert) ─────────────────────────────────────────────────────────
    public async Task<object> SaveAsync(
        string name, string? project, string? args, string? configuration, bool noBuild,
        Dictionary<string, string>? environment)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new { error = "A configuration name is required." };

        name = name.Trim();
        if (name.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            return new { error = $"'{name}' is not a usable configuration name: use letters, digits, '.', '-' or '_'." };

        if (!string.IsNullOrWhiteSpace(project))
        {
            var solution = await _workspace.EnsureSolutionLoadedAsync();
            var known = solution?.Projects.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, project, StringComparison.OrdinalIgnoreCase));
            if (solution is not null && known is null)
                return new
                {
                    error = $"Project '{project}' not found in the solution.",
                    knownProjects = solution.Projects.Select(candidate => candidate.Name),
                };
        }

        var (path, store) = await ReadAsync();
        store ??= new RunConfigurationStore();

        var replaced = store.Configs.ContainsKey(name);
        store.Configs[name] = new RunConfigurationEntry
        {
            Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim(),
            Args = string.IsNullOrWhiteSpace(args) ? null : args,
            Configuration = string.IsNullOrWhiteSpace(configuration) ? null : configuration.Trim(),
            NoBuild = noBuild,
            Environment = environment is { Count: > 0 }
                ? new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase)
                : null,
        };

        await WriteAsync(path, store);

        return new
        {
            saved = true,
            replaced,
            name,
            configsPath = path,
            config = store.Configs[name],
            next = $"Run it with run_project configName='{name}'.",
        };
    }

    // ── delete ────────────────────────────────────────────────────────────────
    public async Task<object> DeleteAsync(string name)
    {
        var (path, store) = await ReadAsync();
        if (store is null || string.IsNullOrWhiteSpace(name) || !store.Configs.Remove(name))
            return new { error = $"No run configuration named '{name}'.", configsPath = path };

        await WriteAsync(path, store);
        return new { removed = true, name, configsPath = path, remaining = store.Configs.Keys.ToList() };
    }

    // ── resolve (used by run_project) ────────────────────────────────────────
    public async Task<(RunConfiguration? Configuration, object? Error)> ResolveAsync(string name)
    {
        var (path, store) = await ReadAsync();
        if (store is null || !store.Configs.TryGetValue(name, out var entry))
            return (null, new
            {
                error = $"No run configuration named '{name}'.",
                configsPath = path,
                knownConfigs = store?.Configs.Keys.ToList() ?? new List<string>(),
            });

        return (new RunConfiguration(
            name,
            entry.Project,
            entry.Args,
            entry.Configuration,
            entry.NoBuild,
            entry.Environment ?? new Dictionary<string, string>()), null);
    }

    // ── storage ───────────────────────────────────────────────────────────────
    private async Task<(string Path, RunConfigurationStore? Store)> ReadAsync()
    {
        var path = BuildPath();
        if (path is null || !File.Exists(path)) return (path ?? "(.visualMCP/run-configs.json)", null);

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var store = JsonSerializer.Deserialize<RunConfigurationStore>(text, WriteOptions) ?? new RunConfigurationStore();

            // The comparer is not part of the JSON: re-key it so "api" and "Api" are the same config.
            store.Configs = new Dictionary<string, RunConfigurationEntry>(store.Configs, StringComparer.OrdinalIgnoreCase);
            return (path, store);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return (path, null);
        }
    }

    private async Task WriteAsync(string path, RunConfigurationStore store)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(store, WriteOptions) + Environment.NewLine;
        await FileText.WriteAsync(path, json);
    }

    private string? BuildPath()
    {
        var solutionPath = _workspace.LoadedSolutionPath ?? _workspace.DiscoverSolutionPath();
        return solutionPath is null
            ? null
            : Path.Combine(Path.GetDirectoryName(solutionPath)!, FolderName, FileName);
    }

    private sealed class RunConfigurationStore
    {
        public Dictionary<string, RunConfigurationEntry> Configs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RunConfigurationEntry
    {
        public string? Project { get; set; }
        public string? Args { get; set; }
        public string? Configuration { get; set; }
        public bool NoBuild { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
    }
}
