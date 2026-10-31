using System.Text.Json;
using System.Text.Json.Nodes;
using Snitch.Platform;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// <c>Properties/launchSettings.json</c> as a tool: list the profiles with the values the app is
/// launched with, add or update one (application URL, command-line arguments, environment variables),
/// remove one. The rest of the document — the <c>$schema</c> line, <c>iisSettings</c>, profiles nobody
/// touched — is preserved.
/// </summary>
public interface ILaunchSettingsService
{
    Task<object> ListAsync(string? projectName, string? projectPath, string? profileName);
    Task<object> SetAsync(
        string? projectName, string? projectPath, string profileName, string? commandName,
        string? applicationUrl, string? commandLineArgs, Dictionary<string, string>? environmentVariables,
        bool replaceEnvironmentVariables, bool dryRun);
    Task<object> RemoveAsync(string? projectName, string? projectPath, string profileName, bool confirm, bool dryRun);
}

public sealed class LaunchSettingsService : ILaunchSettingsService
{
    private const string Schema = "https://json.schemastore.org/launchsettings.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public LaunchSettingsService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    // ── read ──────────────────────────────────────────────────────────────────
    public async Task<object> ListAsync(string? projectName, string? projectPath, string? profileName)
    {
        var (directory, error) = await ProjectLocation.ResolveDirectoryAsync(_workspace, projectName, projectPath);
        if (error is not null) return error;

        var path = Path.Combine(directory!, "Properties", "launchSettings.json");
        if (!File.Exists(path))
            return new
            {
                path,
                exists = false,
                profiles = Array.Empty<object>(),
                note = "This project has no Properties/launchSettings.json yet. action=set creates it.",
            };

        var root = await ReadAsync(path);
        if (root is null)
            return new { error = $"'{path}' is not valid JSON.", path };

        var profiles = Profiles(root);
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            if (!profiles.TryGetValue(profileName, out var profile))
                return new
                {
                    path,
                    error = $"Profile '{profileName}' not found.",
                    knownProfiles = profiles.Keys.ToList(),
                };

            return new { path, exists = true, profile = Describe(profileName, profile) };
        }

        return new
        {
            path,
            exists = true,
            profiles = profiles
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => Describe(pair.Key, pair.Value))
                .ToList(),
            note = "These are the profiles 'dotnet run --launch-profile <name>' and the IDE launch with.",
        };
    }

    // ── set (create or update) ────────────────────────────────────────────────
    public async Task<object> SetAsync(
        string? projectName, string? projectPath, string profileName, string? commandName,
        string? applicationUrl, string? commandLineArgs, Dictionary<string, string>? environmentVariables,
        bool replaceEnvironmentVariables, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return new { error = "A profile name is required." };

        var (directory, error) = await ProjectLocation.ResolveDirectoryAsync(_workspace, projectName, projectPath);
        if (error is not null) return error;

        var path = Path.Combine(directory!, "Properties", "launchSettings.json");
        var existed = File.Exists(path);
        var original = existed ? await File.ReadAllTextAsync(path) : null;

        var root = existed ? await ReadAsync(path) : new JsonObject
        {
            ["$schema"] = Schema,
            ["profiles"] = new JsonObject(),
        };

        if (root is null)
            return new { error = $"'{path}' is not valid JSON; fix it before adding a profile.", path };

        if (root["profiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
        }

        var created = profiles[profileName] is not JsonObject;
        var profile = profiles[profileName] as JsonObject ?? new JsonObject { ["commandName"] = "Project" };
        if (!string.IsNullOrWhiteSpace(commandName)) profile["commandName"] = commandName;
        if (profile["commandName"] is null) profile["commandName"] = "Project";
        if (!string.IsNullOrWhiteSpace(applicationUrl)) profile["applicationUrl"] = applicationUrl;
        if (!string.IsNullOrWhiteSpace(commandLineArgs)) profile["commandLineArgs"] = commandLineArgs;

        if (environmentVariables is { Count: > 0 } variables)
        {
            if (replaceEnvironmentVariables || profile["environmentVariables"] is not JsonObject environment)
            {
                environment = new JsonObject();
                profile["environmentVariables"] = environment;
            }

            foreach (var (name, value) in variables)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return new { error = "An environment variable name cannot be empty." };

                environment[name] = value;
            }
        }

        profiles[profileName] = profile;

        var updated = root.ToJsonString(WriteOptions) + Environment.NewLine;
        if (dryRun)
            return new
            {
                path,
                profile = Describe(profileName, profile),
                created,
                fileCreated = !existed,
                dryRun = true,
                preview = updated,
                note = "Nothing was written. Call again with dryRun=false to apply.",
            };

        var parent = Path.GetDirectoryName(path);
        if (parent is not null) Directory.CreateDirectory(parent);
        await FileText.WriteAsync(path, updated);
        Record(path, profileName, original, updated);

        return new
        {
            path,
            profile = Describe(profileName, profile),
            created,
            fileCreated = !existed,
            succeeded = true,
        };
    }

    // ── remove ────────────────────────────────────────────────────────────────
    public async Task<object> RemoveAsync(
        string? projectName, string? projectPath, string profileName, bool confirm, bool dryRun)
    {
        var (directory, error) = await ProjectLocation.ResolveDirectoryAsync(_workspace, projectName, projectPath);
        if (error is not null) return error;

        var path = Path.Combine(directory!, "Properties", "launchSettings.json");
        if (!File.Exists(path))
            return new { error = "This project has no Properties/launchSettings.json.", path };

        var root = await ReadAsync(path);
        if (root is null) return new { error = $"'{path}' is not valid JSON.", path };

        var profiles = Profiles(root);
        if (!profiles.TryGetValue(profileName, out var profile))
            return new { error = $"Profile '{profileName}' not found.", path, knownProfiles = profiles.Keys.ToList() };

        if (!confirm)
            return new
            {
                path,
                profile = Describe(profileName, profile),
                dryRun = true,
                confirmRequired = true,
                note = "Nothing was removed. Call again with confirm=true to delete the profile.",
            };

        if (dryRun)
            return new { path, profile = profileName, removed = false, dryRun = true, note = "Nothing was removed: dryRun is on." };

        var original = await File.ReadAllTextAsync(path);
        (root["profiles"] as JsonObject)?.Remove(profileName);
        var updated = root.ToJsonString(WriteOptions) + Environment.NewLine;

        await FileText.WriteAsync(path, updated);
        Record(path, profileName, original, updated);

        return new { path, profile = profileName, removed = true, remaining = (root["profiles"] as JsonObject)?.Select(pair => pair.Key).ToList() };
    }

    // ── shapes ────────────────────────────────────────────────────────────────
    private static Dictionary<string, JsonObject> Profiles(JsonObject root)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (root["profiles"] is not JsonObject profiles) return result;

        foreach (var (name, value) in profiles)
            if (value is JsonObject profile)
                result[name] = profile;

        return result;
    }

    private static object Describe(string name, JsonObject profile) => new
    {
        name,
        commandName = Text(profile, "commandName"),
        applicationUrl = Text(profile, "applicationUrl"),
        commandLineArgs = Text(profile, "commandLineArgs"),
        workingDirectory = Text(profile, "workingDirectory"),
        launchBrowser = profile["launchBrowser"]?.GetValue<bool>(),
        environmentVariables = profile["environmentVariables"] is JsonObject environment
            ? environment.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? "")
            : null,
    };

    private static string? Text(JsonObject profile, string name) =>
        profile[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static async Task<JsonObject?> ReadAsync(string path)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private void Record(string path, string profile, string? before, string after)
    {
        try { if (before is not null) _store?.RecordEdit("launch_settings", path, profile, before, after); }
        catch (Exception) { /* the history is an audit trail, not a precondition */ }
    }
}
