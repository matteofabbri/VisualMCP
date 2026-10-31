using System.Xml.Linq;
using Snitch.Platform;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// <c>Properties/PublishProfiles/*.pubxml</c> as a tool: list the profiles with the properties each one
/// sets, read one in full, create or update one, delete one. The properties are the same MSBuild names
/// <c>dotnet publish -p:PublishProfile=&lt;name&gt;</c> reads, so editing a profile here is how a
/// ClickOnce or filesystem publish target is authored without an IDE.
/// </summary>
public interface IPublishProfilesService
{
    Task<object> ListAsync(string? projectName, string? projectPath, string? profileName);
    Task<object> SetAsync(
        string? projectName, string? projectPath, string profileName,
        Dictionary<string, string> properties, bool createIfMissing, bool dryRun);
    Task<object> DeleteAsync(
        string? projectName, string? projectPath, string profileName, bool confirm);
}

public sealed class PublishProfilesService : IPublishProfilesService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public PublishProfilesService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    // ── read ──────────────────────────────────────────────────────────────────
    public async Task<object> ListAsync(string? projectName, string? projectPath, string? profileName)
    {
        var (directory, error) = await ResolveProfilesDirectoryAsync(projectName, projectPath);
        if (error is not null) return error;

        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.pubxml", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (!string.IsNullOrWhiteSpace(profileName))
        {
            var wanted = Normalize(profileName);
            var file = files.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileNameWithoutExtension(candidate), wanted, StringComparison.OrdinalIgnoreCase));

            if (file is null)
                return new
                {
                    profilesDirectory = directory,
                    error = $"Publish profile '{wanted}' not found.",
                    knownProfiles = files.Select(candidate => Path.GetFileNameWithoutExtension(candidate)).ToList(),
                };

            return new
            {
                profilesDirectory = directory,
                profile = new
                {
                    name = Path.GetFileNameWithoutExtension(file),
                    file,
                    properties = ReadProperties(file),
                },
            };
        }

        return new
        {
            profilesDirectory = directory,
            exists = Directory.Exists(directory) && files.Count > 0,
            profiles = files.Select(file => (object)new
            {
                name = Path.GetFileNameWithoutExtension(file),
                file,
                lastModified = File.GetLastWriteTime(file),
                properties = ReadProperties(file),
            }).ToList(),
            note = directory is not null && !Directory.Exists(directory)
                ? "This project has no Properties/PublishProfiles folder yet; action=set creates the first profile."
                : null,
        };
    }

    // ── set (create or update) ────────────────────────────────────────────────
    public async Task<object> SetAsync(
        string? projectName, string? projectPath, string profileName,
        Dictionary<string, string> properties, bool createIfMissing, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return new { error = "A profile name is required." };
        if (properties is null || properties.Count == 0)
            return new { error = "At least one property is required (e.g. {\"PublishDir\": \"bin/publish\"})." };

        foreach (var propertyName in properties.Keys)
            if (!MsBuildPropertyFile.IsValidName(propertyName))
                return new { error = $"'{propertyName}' is not a valid MSBuild property name." };

        var (directory, error) = await ResolveProfilesDirectoryAsync(projectName, projectPath);
        if (error is not null) return error;

        var name = Normalize(profileName);
        var path = Path.Combine(directory!, name + ".pubxml");
        var existed = File.Exists(path);

        if (!existed && !createIfMissing)
            return new { error = $"Publish profile '{name}' does not exist. Pass createIfMissing=true to create it.", path };

        var original = existed
            ? await File.ReadAllTextAsync(path)
            : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project>\n  <PropertyGroup>\n    <WebPublishMethod>FileSystem</WebPublishMethod>\n  </PropertyGroup>\n</Project>\n";

        var updated = original;
        var changes = new List<object>();
        var refused = new List<object>();

        foreach (var (property, value) in properties)
        {
            if (MsBuildPropertyFile.TrySet(updated, property, value, out var next, out var previous, out var note))
            {
                changes.Add(new { property, from = previous, to = value });
                updated = next;
            }
            else
            {
                refused.Add(new { property, reason = note });
            }
        }

        var changed = !string.Equals(original, updated, StringComparison.Ordinal);

        if (dryRun)
            return new
            {
                path,
                name,
                createdFile = !existed,
                changed,
                changes,
                refused,
                dryRun = true,
                note = "Nothing was written. Call again with dryRun=false to apply.",
            };

        if (!changed && existed)
            return new { path, name, changed = false, note = "Nothing to change: the profile already holds these values." };

        var parent = Path.GetDirectoryName(path);
        if (parent is not null) Directory.CreateDirectory(parent);
        await FileText.WriteAsync(path, updated);
        Record(path, name, existed ? original : null, updated);

        return new
        {
            path,
            name,
            createdFile = !existed,
            changed = true,
            changes,
            refused,
            succeeded = true,
            next = $"Publish with dotnet_publish, or 'dotnet publish -p:PublishProfile={name}'.",
        };
    }

    // ── delete ────────────────────────────────────────────────────────────────
    public async Task<object> DeleteAsync(
        string? projectName, string? projectPath, string profileName, bool confirm)
    {
        var (directory, error) = await ResolveProfilesDirectoryAsync(projectName, projectPath);
        if (error is not null) return error;

        var name = Normalize(profileName);
        var path = Path.Combine(directory!, name + ".pubxml");
        if (!File.Exists(path))
            return new { error = $"Publish profile '{name}' does not exist.", path };

        if (!confirm)
            return new
            {
                path,
                confirmRequired = true,
                note = "Nothing was deleted. Call again with confirm=true to remove the profile file.",
            };

        File.Delete(path);

        var userFile = path + ".user";
        if (File.Exists(userFile)) File.Delete(userFile);

        return new { path, name, removed = true };
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private async Task<(string? Directory, object? Error)> ResolveProfilesDirectoryAsync(
        string? projectName, string? projectPath)
    {
        var (directory, error) = await ProjectLocation.ResolveDirectoryAsync(_workspace, projectName, projectPath);
        return error is not null ? (null, error) : (Path.Combine(directory!, "Properties", "PublishProfiles"), null);
    }

    private static Dictionary<string, string> ReadProperties(string file)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var document = XDocument.Load(file);
            foreach (var group in document.Root?.Elements().Where(element => element.Name.LocalName == "PropertyGroup") ?? [])
                foreach (var property in group.Elements())
                    properties[property.Name.LocalName] = property.Value;
        }
        catch (Exception exception) when (exception is IOException or System.Xml.XmlException)
        {
            properties["(unreadable)"] = exception.Message;
        }

        return properties;
    }

    private static string Normalize(string profileName)
    {
        var name = profileName.Trim();
        return name.EndsWith(".pubxml", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
    }

    private void Record(string path, string profile, string? before, string after)
    {
        try { if (before is not null) _store?.RecordEdit("publish_profile", path, profile, before, after); }
        catch (Exception) { /* the history is an audit trail, not a precondition */ }
    }
}
