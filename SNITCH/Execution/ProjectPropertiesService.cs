using System.Text.Json;
using Snitch.Languages.CSharp.Modernization;
using Snitch.Platform;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// The evaluated view of a project (the value MSBuild computes, not what the file spells) and the
/// write side: one property set or removed in the project file or in the shared
/// <c>Directory.Build.props</c>, compiler-gated and revertible.
/// </summary>
public interface IProjectPropertiesService
{
    Task<object> GetAsync(
        string? projectName, string? projectPath, string[]? properties, string[]? items,
        string? configuration, string? platform, int timeoutSeconds);

    Task<object> SetAsync(
        string? projectName, string? projectPath, string propertyName, string? value, bool remove,
        string scope, bool dryRun, bool buildGate, int timeoutSeconds);
}

public sealed class ProjectPropertiesService : IProjectPropertiesService
{
    /// <summary>
    /// What a caller normally wants from a project and would otherwise run three MSBuild invocations
    /// to assemble: the target framework, its output shape, the language options and the package id.
    /// </summary>
    private static readonly string[] DefaultProperties =
    [
        "TargetFramework", "TargetFrameworks", "Configuration", "Platform", "OutputType",
        "AssemblyName", "RootNamespace", "Nullable", "ImplicitUsings", "LangVersion",
        "OutputPath", "Version", "PackageId", "IsPackable",
        "RuntimeIdentifier", "SelfContained", "PublishSingleFile",
        "TreatWarningsAsErrors", "GenerateDocumentationFile", "UseArtifactsOutput", "ArtifactsPath",
    ];

    private readonly IRoslynWorkspace _workspace;
    private readonly IBuildProjectService _build;
    private readonly IVisualMcpStore? _store;

    public ProjectPropertiesService(IRoslynWorkspace workspace, IBuildProjectService build, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _build = build;
        _store = store;
    }

    // ── read: the evaluated view ─────────────────────────────────────────────
    public async Task<object> GetAsync(
        string? projectName, string? projectPath, string[]? properties, string[]? items,
        string? configuration, string? platform, int timeoutSeconds)
    {
        var (project, error) = await ResolveAsync(projectName, projectPath);
        if (error is not null) return error;

        var names = (properties ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToList();
        if (names.Count == 0) names = [.. DefaultProperties];

        foreach (var name in names)
            if (!MsBuildPropertyFile.IsValidName(name))
                return new { error = $"'{name}' is not a valid MSBuild property name." };

        foreach (var item in items ?? [])
            if (!MsBuildPropertyFile.IsValidName(item))
                return new { error = $"'{item}' is not a valid MSBuild item name." };

        var parts = new List<string> { "msbuild", Quote(project!), "-nologo", "-getProperty:" + string.Join(";", names) };
        if (items is { Length: > 0 }) parts.Add("-getItem:" + string.Join(";", items));
        if (!string.IsNullOrWhiteSpace(configuration)) parts.Add($"-p:Configuration={configuration}");
        if (!string.IsNullOrWhiteSpace(platform)) parts.Add($"-p:Platform={platform}");

        var command = "dotnet " + string.Join(" ", parts);
        var (exitCode, timedOut, stdout, stderr, elapsed) = await ProcessRunner.RunAsync(
            "dotnet", string.Join(" ", parts), Path.GetDirectoryName(project!)!, Clamp(timeoutSeconds));

        if (timedOut)
            return new { error = $"dotnet msbuild timed out after {Clamp(timeoutSeconds)}s.", command };

        if (exitCode != 0)
            return new
            {
                error = $"dotnet msbuild failed (exit {exitCode}).",
                command,
                output = ProcessRunner.Truncate((stdout + "\n" + stderr).Trim(), 4000),
            };

        if (!TryParseEvaluation(stdout, out var values, out var itemValues))
            return new
            {
                error = "dotnet msbuild returned output that is not the expected evaluation JSON.",
                command,
                output = ProcessRunner.Truncate(stdout.Trim(), 4000),
            };

        return new
        {
            project = Path.GetFileNameWithoutExtension(project),
            projectPath = project,
            command,
            durationMs = (long)elapsed.TotalMilliseconds,
            properties = values,
            items = itemValues,
            note = "These are the evaluated values MSBuild computes with the given Configuration/Platform " +
                   "(including what Directory.Build.props and the SDK contribute), not the literal file text. " +
                   "Property names that were not evaluated come back empty.",
        };
    }

    // ── write: one property, gated ───────────────────────────────────────────
    public async Task<object> SetAsync(
        string? projectName, string? projectPath, string propertyName, string? value, bool remove,
        string scope, bool dryRun, bool buildGate, int timeoutSeconds)
    {
        if (!MsBuildPropertyFile.IsValidName(propertyName))
            return new { error = $"'{propertyName}' is not a valid MSBuild property name." };

        if (!remove && value is null)
            return new { error = "A value is required for action=set; pass remove=true (action=unset) to delete the property." };

        var (project, error) = await ResolveAsync(projectName, projectPath);
        if (error is not null) return error;

        var normalizedScope = (scope ?? "project").Trim().ToLowerInvariant();
        string file;
        if (normalizedScope is "props" or "directory" or "directory.build.props")
        {
            var directory = Path.GetDirectoryName(project!)!;
            file = ProjectFiles.FindUpwards(directory, "Directory.Build.props")
                   ?? "";
            if (file.Length == 0)
                return new
                {
                    error = $"No Directory.Build.props found walking up from '{directory}'. " +
                            "Create it first (or use scope='project') rather than having this tool invent a shared file.",
                };
        }
        else if (normalizedScope is "project" or "csproj")
        {
            file = project!;
        }
        else
        {
            return new { error = $"Unknown scope '{scope}'. Use 'project' (the .csproj) or 'props' (the nearest Directory.Build.props)." };
        }

        var original = await File.ReadAllTextAsync(file);

        string updated;
        string? removedValue = null;
        string? previous = null;
        string? note;
        bool applied;

        if (remove)
        {
            applied = MsBuildPropertyFile.TryRemove(original, propertyName, out updated, out removedValue, out note);
        }
        else
        {
            applied = MsBuildPropertyFile.TrySet(original, propertyName, value!, out updated, out previous, out note);
        }

        if (!applied)
            return new { file, property = propertyName, changed = false, note };

        var changed = !string.Equals(original, updated, StringComparison.Ordinal);
        var plan = new
        {
            file,
            property = propertyName,
            from = remove ? removedValue : previous,
            to = remove ? null : value,
            inserted = !remove && previous is null,
            changed,
            dryRun,
        };

        if (!changed)
            return new { plan.file, plan.property, plan.from, plan.to, plan.inserted, changed = false, note = "Nothing to change: the file already holds this value." };

        if (dryRun)
            return new
            {
                plan.file, plan.property, plan.from, plan.to, plan.inserted, plan.changed, dryRun = true,
                note = "Nothing was written. Call again with dryRun=false to apply" +
                       (buildGate ? " (the build gate restores the file when the solution stops compiling)." : "; the build gate is off."),
            };

        await FileText.WriteAsync(file, updated);
        Record(remove ? "project_properties (unset)" : "project_properties", file, propertyName, original, updated);

        object? build = null;
        if (buildGate)
        {
            var (ok, result) = await GateAsync(timeoutSeconds);
            build = result;
            if (!ok)
            {
                await FileText.WriteAsync(file, original);
                await ReloadAsync();
                return new
                {
                    error = $"The solution does not build with {propertyName} set that way; {Path.GetFileName(file)} was restored.",
                    reverted = true,
                    plan.file, plan.property, plan.from, plan.to, build,
                };
            }
        }

        await ReloadAsync();
        return new
        {
            plan.file, plan.property, plan.from, plan.to, plan.inserted, succeeded = true,
            build,
            next = "Call project_properties action=get to read the evaluated value MSBuild now computes.",
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private async Task<(string? Project, object? Error)> ResolveAsync(string? projectName, string? projectPath)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var full = Path.GetFullPath(projectPath);
            return File.Exists(full)
                ? (full, null)
                : (null, new { error = $"Project file '{projectPath}' does not exist." });
        }

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return (null, new { error = "No C# solution could be auto-located. Call load_solution with an explicit path, or pass projectPath." });

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var named = solution.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (named?.FilePath is null)
                return (null, new { error = $"Project '{projectName}' not found in the solution.", knownProjects = solution.Projects.Select(project => project.Name) });

            return (named.FilePath, null);
        }

        var withPath = solution.Projects.Where(project => project.FilePath is not null).ToList();
        return withPath.Count == 1
            ? (withPath[0].FilePath, null)
            : (null, new { error = "The solution has more than one project; pass projectName (or projectPath).", knownProjects = withPath.Select(project => project.Name) });
    }

    private static bool TryParseEvaluation(string output, out Dictionary<string, string?> properties, out Dictionary<string, JsonElement> items)
    {
        properties = [];
        items = [];

        var start = output.IndexOf('{');
        if (start < 0) return false;

        try
        {
            using var document = JsonDocument.Parse(output[start..]);
            var root = document.RootElement;

            if (root.TryGetProperty("Properties", out var propertyObject) && propertyObject.ValueKind == JsonValueKind.Object)
                foreach (var property in propertyObject.EnumerateObject())
                    properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.ToString();

            if (root.TryGetProperty("Items", out var itemObject) && itemObject.ValueKind == JsonValueKind.Object)
                foreach (var item in itemObject.EnumerateObject())
                    items[item.Name] = item.Value.Clone();

            return properties.Count > 0 || items.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(bool Ok, object? Build)> GateAsync(int timeoutSeconds)
    {
        var result = await _build.RunAsync(null, "Debug", noCopyOutput: false, restore: true, Clamp(timeoutSeconds), runAnalyzers: false);

        try
        {
            var json = JsonSerializer.SerializeToElement(result);
            var succeeded = json.TryGetProperty("succeeded", out var property) && property.ValueKind == JsonValueKind.True;
            return (succeeded, result);
        }
        catch (Exception)
        {
            return (false, result);
        }
    }

    private void Record(string tool, string file, string property, string before, string after)
    {
        try { _store?.RecordEdit(tool, file, property, before, after); }
        catch (Exception) { /* the history is an audit trail, not a precondition */ }
    }

    private async Task ReloadAsync()
    {
        if (_workspace.LoadedSolutionPath is not { } solutionPath) return;
        try { await _workspace.LoadSolutionAsync(solutionPath); }
        catch { /* the next tool reloads on demand */ }
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static int Clamp(int seconds) => seconds < 1 ? 1 : seconds > 900 ? 900 : seconds;
}
