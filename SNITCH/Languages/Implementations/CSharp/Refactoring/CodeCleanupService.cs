using System.Text.Json;
using Snitch.Languages.CSharp.Style;
using Snitch.Languages.VB.Style;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// ReSharper's Code Cleanup as one call: the pieces already existed as separate tools (the style
/// rewrites, using pruning, region removal, member reordering, formatting), and cleanup is the order
/// they belong in. It is deliberately not a new engine — every step is the tool it was, run in the
/// scope the caller asked for, and the report is the concatenation of their reports.
/// <para>
/// A cleanup profile is a named step list. The built-in ones (<c>full</c>, <c>reformat</c>,
/// <c>style</c>, <c>usings</c>, <c>regions</c>, <c>reorder</c>, <c>safe</c>) cover the profiles
/// ReSharper ships; a team adds its own in <c>.visualMCP/cleanup-profiles.json</c>, and
/// <c>saveProfileAs</c> writes one there — the same "named policy" an IDE stores in its settings.
/// </para>
/// <para>
/// The language-neutral steps (usings, regions, format) cover both C# and Visual Basic; the style step
/// runs each language's rule set over its own documents, and member reordering is C# only because the
/// order it enforces is C#'s own convention.
/// </para>
/// </summary>
public interface ICodeCleanupService
{
    Task<object> Cleanup(
        string scope,
        string? filePath,
        string? projectName,
        string? steps,
        bool dryRun,
        string? profile = null,
        string? saveProfileAs = null,
        CancellationToken cancellationToken = default);
}

public sealed class CodeCleanupService : ICodeCleanupService
{
    /// <summary>The steps in the order they must run: rewriting first, formatting last.</summary>
    private static readonly string[] Order = ["style", "usings", "regions", "reorder", "format"];

    /// <summary>The profiles ReSharper ships, as the step lists they mean here.</summary>
    private static readonly Dictionary<string, string[]> BuiltInProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        // ReSharper's "Full Cleanup": every step, in the order below.
        ["full"] = Order,
        // "Reformat Code": whitespace and layout only.
        ["reformat"] = ["format"],
        // "Reformat & Apply Style": the style rewrites then formatting.
        ["style"] = ["style", "format"],
        ["usings"] = ["usings"],
        ["regions"] = ["regions"],
        ["reorder"] = ["reorder"],
        // A conservative profile: the steps that cannot change code shape.
        ["safe"] = ["usings", "regions", "format"],
    };

    private readonly ISimplifyCodeService _style;
    private readonly IOptimizeUsingsService _usings;
    private readonly IRemoveRegionsService _regions;
    private readonly IReorderMembersService _reorder;
    private readonly IFormatCodeService _format;
    private readonly IRoslynWorkspace? _workspace;
    private readonly IVisualMcpStore? _store;

    public CodeCleanupService(
        ISimplifyCodeService style,
        IOptimizeUsingsService usings,
        IRemoveRegionsService regions,
        IReorderMembersService reorder,
        IFormatCodeService format,
        IRoslynWorkspace? workspace = null,
        IVisualMcpStore? store = null)
    {
        _style = style;
        _usings = usings;
        _regions = regions;
        _reorder = reorder;
        _format = format;
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> Cleanup(
        string scope,
        string? filePath,
        string? projectName,
        string? steps,
        bool dryRun,
        string? profile = null,
        string? saveProfileAs = null,
        CancellationToken cancellationToken = default)
    {
        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("document" or "project" or "solution"))
            return new { error = "scope must be document, project or solution." };

        if (!string.IsNullOrWhiteSpace(steps) && !string.IsNullOrWhiteSpace(profile))
            return new { error = "Pass steps or profile, not both: a profile is a named step list." };

        var (customProfiles, profilesFile, profileErrors, loadError) = await LoadProfilesAsync(cancellationToken);
        if (loadError is not null) return loadError;

        var available = AvailableProfiles(customProfiles);

        string? resolvedProfile = null;
        string[]? profileSteps = null;
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var name = profile.Trim();
            if (BuiltInProfiles.TryGetValue(name, out var builtIn))
            {
                resolvedProfile = name.ToLowerInvariant();
                profileSteps = builtIn;
            }
            else if (customProfiles.TryGetValue(name, out var custom))
            {
                resolvedProfile = customProfiles.Keys.First(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
                profileSteps = custom;
            }
            else
            {
                return new
                {
                    error = $"Unknown cleanup profile '{name}'.",
                    availableProfiles = available,
                    profileFile = profilesFile,
                    errors = profileErrors,
                };
            }
        }

        var requested = profileSteps is not null
            ? profileSteps.ToList()
            : string.IsNullOrWhiteSpace(steps)
                ? Order.ToList()
                : steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(step => step.ToLowerInvariant())
                       .Distinct()
                       .ToList();

        var unknown = requested.Except(Order, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            return new
            {
                error = (resolvedProfile is null ? string.Empty : $"Profile '{resolvedProfile}': ") +
                        $"unknown cleanup step(s): {string.Join(", ", unknown)}.",
                supported = Order,
                profiles = available,
            };

        string? savedProfile = null;
        if (!string.IsNullOrWhiteSpace(saveProfileAs))
        {
            var saved = await SaveProfileAsync(saveProfileAs.Trim(), requested, profilesFile, dryRun, cancellationToken);
            if (saved.Error is not null) return new { error = saved.Error, profileFile = profilesFile };
            savedProfile = saved.Name;
        }

        var ordered = Order.Where(requested.Contains).ToList();
        var path = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);

        var results = new List<object>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countedFiles = 0;

        foreach (var step in ordered)
        {
            object result = step switch
            {
                "style" => await StyleAsync(level, path, projectName, dryRun, cancellationToken),
                "usings" => await _usings.OptimizeUsings(projectName, path, sort: true, dryRun),
                "regions" => await _regions.RemoveRegions(projectName, path, dryRun),
                "reorder" => await _reorder.ReorderMembers(projectName, path, dryRun),
                _ => await _format.Format(level, path, projectName, dryRun, cancellationToken),
            };

            var element = JsonSerializer.SerializeToElement(result);
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out var error))
                return new
                {
                    error = $"{step}: {error.GetString()}",
                    completed = results,
                    note = "Cleanup stopped at the first failing step; earlier steps have already run.",
                };

            var stepFiles = FilesOf(element).ToList();
            countedFiles += CountOf(element, "filesModified") + CountOf(element, "filesChanged");
            foreach (var file in stepFiles) files.Add(file);

            results.Add(new
            {
                step,
                filesModified = CountOf(element, "filesModified") + CountOf(element, "filesChanged"),
                result,
            });
        }

        return new
        {
            tool = "code_cleanup",
            scope = level,
            profile = resolvedProfile,
            savedProfile,
            steps = ordered,
            dryRun,
            applied = !dryRun,
            filesModified = files.Count > 0 ? files.Count : countedFiles,
            files = files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList(),
            availableProfiles = available,
            profileFile = profilesFile,
            profileErrors,
            results,
            note = dryRun
                ? "Preview only: each step reported what it would change; pass dryRun=false to write."
                : "Steps ran in order (rewrite → usings → regions → reorder → format) and the files were written. " +
                  "Style, usings, regions and format cover C# and Visual Basic; reorder applies to C# declarations.",
        };
    }

    // ── profiles ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>.visualMCP/cleanup-profiles.json</c>: the profiles a team shares. The schema is
    /// <c>{ "profiles": { "name": { "steps": ["style","format"] } } }</c>; a bare array
    /// (<c>"name": ["style"]</c>) is accepted because it is the obvious shorthand.
    /// </summary>
    private async Task<(Dictionary<string, string[]> Profiles, string Path, List<string> Errors, object? Error)>
        LoadProfilesAsync(CancellationToken cancellationToken)
    {
        var root = _workspace is null ? Directory.GetCurrentDirectory() : SolutionPaths.DirectoryOf(_workspace);
        var file = Path.Combine(root, ".visualMCP", "cleanup-profiles.json");
        var profiles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        if (!File.Exists(file)) return (profiles, Path.GetFullPath(file), errors, null);

        string json;
        try { json = await File.ReadAllTextAsync(file, cancellationToken); }
        catch (Exception exception)
        {
            return (profiles, Path.GetFullPath(file), errors,
                new { error = $"Could not read {file}: {exception.Message}" });
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("profiles", out var section) ||
                section.ValueKind != JsonValueKind.Object)
                return (profiles, Path.GetFullPath(file), errors,
                    new
                    {
                        error = $"{file} must be an object with a \"profiles\" object.",
                        schema = "{\"profiles\":{\"team\":{\"steps\":[\"style\",\"usings\",\"format\"]}}}",
                    });

            foreach (var property in section.EnumerateObject())
            {
                var steps = property.Value.ValueKind switch
                {
                    JsonValueKind.Array => property.Value.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)
                        .ToArray(),
                    JsonValueKind.Object when property.Value.TryGetProperty("steps", out var stepsElement) &&
                                               stepsElement.ValueKind == JsonValueKind.Array =>
                        stepsElement.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!)
                            .ToArray(),
                    _ => null,
                };

                if (steps is null)
                {
                    errors.Add($"Profile '{property.Name}' has no step list.");
                    continue;
                }

                profiles[property.Name] = steps;
            }
        }
        catch (JsonException exception)
        {
            return (profiles, Path.GetFullPath(file), errors,
                new { error = $"{file} is not valid JSON: {exception.Message}" });
        }

        return (profiles, Path.GetFullPath(file), errors, null);
    }

    private static Dictionary<string, string[]> AvailableProfiles(Dictionary<string, string[]> custom)
    {
        var all = new Dictionary<string, string[]>(BuiltInProfiles, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, steps) in custom) all[name] = steps;
        return all;
    }

    private async Task<(string? Name, string? Error)> SaveProfileAsync(
        string name, List<string> steps, string file, bool dryRun, CancellationToken cancellationToken)
    {
        if (name.Length == 0 || !name.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
            return (null, $"'{name}' is not a profile name (letters, digits, '-', '_' and '.' only).");

        if (BuiltInProfiles.ContainsKey(name))
            return (null, $"'{name}' is a built-in profile; save the custom one under another name.");

        if (dryRun)
            return (name, null);

        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var profiles = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(file))
            {
                var (existing, _, _, _) = await LoadProfilesAsync(cancellationToken);
                foreach (var (existingName, existingSteps) in existing)
                    profiles[existingName] = new { steps = existingSteps };
            }

            profiles[name] = new { steps };

            var json = JsonSerializer.Serialize(
                new { profiles }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file, json + Environment.NewLine, cancellationToken);
            return (name, null);
        }
        catch (Exception exception)
        {
            return (null, $"Could not write {file}: {exception.Message}");
        }
    }

    /// <summary>
    /// The style step in both languages: each engine reads only the documents of its own language, so
    /// one call over a mixed solution rewrites both. The report keeps the two halves apart.
    /// </summary>
    private async Task<object> StyleAsync(
        string level, string? path, string? projectName, bool dryRun, CancellationToken cancellationToken)
    {
        var csharp = await _style.SimplifyAsync(level, path, projectName, rules: null, dryRun, cancellationToken);
        if (_workspace is null) return csharp;

        var visualBasic = await new VbSimplifyCodeService(_workspace, _store)
            .SimplifyAsync(level, path, projectName, rules: null, dryRun, cancellationToken);

        var csharpJson = JsonSerializer.SerializeToElement(csharp);
        var visualBasicJson = JsonSerializer.SerializeToElement(visualBasic);

        if (ErrorOf(csharpJson) is { } csharpError)
            return new { error = csharpError, csharp, visualBasic };

        if (ErrorOf(visualBasicJson) is { } visualBasicError)
            return new { error = visualBasicError, csharp, visualBasic };

        return new
        {
            csharp,
            visualBasic,
            filesChanged = CountOf(csharpJson, "filesChanged") + CountOf(visualBasicJson, "filesChanged"),
            files = FilesOf(csharpJson).Concat(FilesOf(visualBasicJson))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static string? ErrorOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;

    /// <summary>The file count a step reported under one of its names, when it reports one.</summary>
    private static int CountOf(JsonElement result, string property) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty(property, out var count) &&
        count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : 0;

    /// <summary>The paths a step reported, whichever shape it used (<c>files</c> of strings or objects).</summary>
    private static IEnumerable<string> FilesOf(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in files.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { Length: > 0 } path) yield return path;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in new[] { "file", "File", "filePath", "FilePath" })
            {
                if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
                if (value.GetString() is { Length: > 0 } path) yield return path;
                break;
            }
        }
    }
}
