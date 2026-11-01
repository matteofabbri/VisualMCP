using Microsoft.CodeAnalysis;
using System.Text.Json;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// The NuGet facts the build already resolved: every package the project actually uses, per target
/// framework, with the dependency edges between them. Read from <c>obj/project.assets.json</c>, so no
/// restore and no network are needed — the file is the same one the compiler sees.
/// </summary>
public interface IPackageFactsService
{
    /// <summary>Re-reads every project's resolved packages into the store.</summary>
    Task<object> RefreshAsync(string? projectName = null, CancellationToken cancellationToken = default);

    object List(string? project = null, string? id = null, bool directOnly = false, int limit = 500);

    /// <summary>The edges that pull a package in, or that a package pulls.</summary>
    object Dependencies(string? project = null, string? packageId = null, int limit = 500);
}

public sealed class PackageFactsService : IPackageFactsService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore _store;

    public PackageFactsService(IRoslynWorkspace workspace, IVisualMcpStore store)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> RefreshAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        if (_store.EnsureDatabase() is null)
            return new { error = "No solution is loaded, so there is no store for the package facts. Call load_solution first." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"No project '{projectName}'.", projects = solution.Projects.Select(p => p.Name).ToList() };

        var read = new List<object>();
        var missing = new List<string>();

        foreach (var project in projects)
        {
            var assetsPath = AssetsPath(project);
            if (assetsPath is null || !File.Exists(assetsPath))
            {
                missing.Add(project.Name);
                continue;
            }

            try
            {
                var (facts, edges) = await ReadAsync(project.Name, assetsPath, cancellationToken);
                _store.ReplacePackages(project.Name, facts);
                _store.ReplacePackageDependencies(project.Name, edges);
                read.Add(new
                {
                    project = project.Name,
                    frameworks = facts.Select(fact => fact.Tfm).Distinct().Count(),
                    packages = facts.Select(fact => fact.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    direct = facts.Where(fact => fact.Direct).Select(fact => fact.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    edges = edges.Count,
                });
            }
            catch (Exception ex)
            {
                missing.Add($"{project.Name} ({ex.Message})");
            }
        }

        return new
        {
            projects = read,
            skipped = missing,
            stored = _store.PackageCount(),
            note = missing.Count > 0
                ? "A project without obj/project.assets.json has not been restored: build it once and refresh again."
                : null,
        };
    }

    public object List(string? project = null, string? id = null, bool directOnly = false, int limit = 500)
    {
        if (_store.DatabasePath is null && _store.EnsureDatabase() is null)
            return new { error = "No solution is loaded, so there are no package facts. Call load_solution first." };

        var facts = _store.Packages(project, 20_000);
        if (!string.IsNullOrWhiteSpace(id))
            facts = facts.Where(fact => fact.Id.Contains(id!, StringComparison.OrdinalIgnoreCase)).ToList();
        if (directOnly)
            facts = facts.Where(fact => fact.Direct).ToList();

        return new
        {
            count = facts.Count,
            returned = Math.Min(limit, facts.Count),
            packages = facts
                .GroupBy(fact => new { fact.Project, fact.Id, fact.Version })
                .Select(group => new
                {
                    project = group.Key.Project,
                    id = group.Key.Id,
                    version = group.Key.Version,
                    direct = group.Any(fact => fact.Direct),
                    frameworks = group.Select(fact => fact.Tfm).Distinct().ToList(),
                })
                .Take(limit)
                .ToList(),
            note = "Resolved versions, per target framework: this is what the build restores, not what the csproj asks for.",
        };
    }

    public object Dependencies(string? project = null, string? packageId = null, int limit = 500)
    {
        if (_store.DatabasePath is null && _store.EnsureDatabase() is null)
            return new { error = "No solution is loaded, so there are no package facts. Call load_solution first." };

        var edges = _store.PackageDependencies(project, packageId, limit);
        return new
        {
            count = edges.Count,
            dependencies = edges.Select(edge => new
            {
                project = edge.Project,
                parent = $"{edge.ParentId} {edge.ParentVersion}",
                framework = edge.Tfm,
                child = $"{edge.ChildId} {edge.ChildVersion}".TrimEnd(),
            }).ToList(),
            note = "With packageId, these are the packages that pull it in; without, the whole resolved graph (paged).",
        };
    }

    private static string? AssetsPath(Project project)
    {
        if (project.FilePath is null) return null;
        var directory = Path.GetDirectoryName(project.FilePath);
        return directory is null ? null : Path.Combine(directory, "obj", "project.assets.json");
    }

    private static async Task<(List<PackageFact> Facts, List<PackageDependency> Edges)> ReadAsync(
        string project, string assetsPath, CancellationToken cancellationToken)
    {
        var facts = new List<PackageFact>();
        var edges = new List<PackageDependency>();

        await using var stream = File.OpenRead(assetsPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (!root.TryGetProperty("targets", out var targets)) return (facts, edges);

        foreach (var target in targets.EnumerateObject())
        {
            var framework = target.Name;
            var direct = DirectIds(root, framework);

            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in target.Value.EnumerateObject())
            {
                if (!TrySplit(entry.Name, out var id, out var version)) continue;
                if (entry.Value.TryGetProperty("type", out var type) && type.GetString() is { } kind && kind != "package")
                    continue;

                resolved[id] = version;
            }

            foreach (var (id, version) in resolved)
                facts.Add(new PackageFact(project, id, version, framework, direct.Contains(id)));

            foreach (var entry in target.Value.EnumerateObject())
            {
                if (!TrySplit(entry.Name, out var parent, out var parentVersion)) continue;
                if (!resolved.ContainsKey(parent)) continue;
                if (!entry.Value.TryGetProperty("dependencies", out var dependencies)) continue;

                foreach (var dependency in dependencies.EnumerateObject())
                {
                    resolved.TryGetValue(dependency.Name, out var childVersion);
                    edges.Add(new PackageDependency(
                        project, parent, parentVersion, framework, dependency.Name, childVersion ?? string.Empty));
                }
            }
        }

        return (facts, edges);
    }

    /// <summary>The ids the project asks for directly, in the <c>projectFileDependencyGroups</c> section.</summary>
    private static HashSet<string> DirectIds(JsonElement root, string framework)
    {
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("projectFileDependencyGroups", out var groups)) return direct;
        if (!groups.TryGetProperty(framework, out var entries) || entries.ValueKind != JsonValueKind.Array) return direct;

        foreach (var entry in entries.EnumerateArray())
        {
            var text = entry.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var id = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (id.Length > 0) direct.Add(id);
        }

        return direct;
    }

    private static bool TrySplit(string key, out string id, out string version)
    {
        id = string.Empty;
        version = string.Empty;

        var slash = key.LastIndexOf('/');
        if (slash <= 0 || slash == key.Length - 1) return false;

        id = key[..slash];
        version = key[(slash + 1)..];
        return true;
    }
}
