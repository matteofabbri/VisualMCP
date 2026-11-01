using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Snitch.Execution;
using Snitch.Languages.CSharp.Packages;
using Snitch.Platform;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Modernization;

/// <summary>
/// Moves a solution forward: the target framework of its projects, then the NuGet packages whose
/// versions belong to that framework.
///
/// Both operations are planned first and applied as one set of text edits, so a failed build can put
/// every file back exactly as it was. That is the point: a half-upgraded solution that does not compile
/// is a worse place to start from than the one the caller had.
/// </summary>
public interface ISolutionUpgradeService
{
    Task<object> UpdateFrameworkAsync(
        string? targetFramework, string[]? projectNames, bool dryRun, bool buildGate,
        bool updateGlobalJson, bool createGlobalJson, int timeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<object> UpdateSolutionAsync(
        string? targetFramework, string[]? projectNames, bool includePrerelease, bool dryRun, bool buildGate,
        bool updateGlobalJson, bool createGlobalJson, int timeoutSeconds,
        CancellationToken cancellationToken = default);
}

public sealed class SolutionUpgradeService : ISolutionUpgradeService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IBuildProjectService _build;
    private readonly IVisualMcpStore _store;

    public SolutionUpgradeService(IRoslynWorkspace workspace, IBuildProjectService build, IVisualMcpStore store)
    {
        _workspace = workspace;
        _build = build;
        _store = store;
    }

    // ── update_framework ──────────────────────────────────────────────────────
    public async Task<object> UpdateFrameworkAsync(
        string? targetFramework, string[]? projectNames, bool dryRun, bool buildGate,
        bool updateGlobalJson, bool createGlobalJson, int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var (plan, error) = await PlanFrameworkAsync(
            targetFramework, projectNames, updateGlobalJson, createGlobalJson, timeoutSeconds, cancellationToken);
        if (error is not null) return error;

        var edits = plan!.Edits;
        if (edits.Count == 0)
            return new
            {
                dryRun,
                targetFramework = plan.TargetFramework,
                changedFiles = 0,
                note = $"Nothing to change: the selected projects already target {plan.TargetFramework} " +
                       "(or have no TargetFramework of their own to bump).",
                notes = plan.Notes,
            };

        var files = edits.Select(edit => edit.ToPlan()).ToList();
        if (dryRun)
            return new
            {
                dryRun = true,
                targetFramework = plan.TargetFramework,
                changedFiles = edits.Count,
                files,
                notes = plan.Notes,
                note = "Nothing was written. Call again with dryRun=false to apply (the build gate reverts on failure).",
            };

        await ApplyAsync(edits);

        object? build = null;
        if (buildGate)
        {
            var (ok, result) = await GateAsync(timeoutSeconds);
            build = result;
            if (!ok)
            {
                await RollbackAsync(edits);
                await ReloadAsync();
                return new
                {
                    error = "The solution does not build after the framework update; every changed file was restored.",
                    reverted = true,
                    targetFramework = plan.TargetFramework,
                    files,
                    build,
                    notes = plan.Notes,
                };
            }
        }

        Record("update_framework", edits);
        await ReloadAsync();

        return new
        {
            succeeded = true,
            targetFramework = plan.TargetFramework,
            changedFiles = edits.Count,
            files,
            build,
            notes = plan.Notes,
            next = "Run update_solution to bring the NuGet packages to versions that support the new target.",
        };
    }

    // ── update_solution ───────────────────────────────────────────────────────
    public async Task<object> UpdateSolutionAsync(
        string? targetFramework, string[]? projectNames, bool includePrerelease, bool dryRun, bool buildGate,
        bool updateGlobalJson, bool createGlobalJson, int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var edits = new List<FileEdit>();
        var notes = new List<object>();
        object? framework = null;

        if (targetFramework is not null)
        {
            var (plan, planError) = await PlanFrameworkAsync(
                targetFramework, projectNames, updateGlobalJson, createGlobalJson, timeoutSeconds, cancellationToken);
            if (planError is not null) return planError;

            edits.AddRange(plan!.Edits);
            notes.AddRange(plan.Notes);
            framework = new
            {
                targetFramework = plan.TargetFramework,
                changedFiles = plan.Edits.Count,
                files = plan.Edits.Select(edit => edit.ToPlan()).ToList(),
            };
        }

        var (packageEdits, packageUpdates, packageNotes, packageError) =
            await PlanPackagesAsync(projectNames, includePrerelease, timeoutSeconds, cancellationToken);
        if (packageError is not null) return packageError;

        edits.AddRange(packageEdits);
        notes.AddRange(packageNotes);

        if (edits.Count == 0)
            return new
            {
                dryRun,
                changedFiles = 0,
                framework,
                packageUpdates,
                note = "Nothing to update: no older target framework and no outdated top-level package.",
                notes,
            };

        var files = edits.Select(edit => edit.ToPlan()).ToList();
        if (dryRun)
            return new
            {
                dryRun = true,
                changedFiles = edits.Count,
                framework,
                files,
                packageUpdates,
                notes,
                note = "Nothing was written. Call again with dryRun=false to apply (the build gate reverts everything on failure).",
            };

        await ApplyAsync(edits);

        object? build = null;
        if (buildGate)
        {
            var (ok, result) = await GateAsync(timeoutSeconds);
            build = result;
            if (!ok)
            {
                await RollbackAsync(edits);
                await ReloadAsync();
                return new
                {
                    error = "The solution does not build after the update; every changed file was restored.",
                    reverted = true,
                    framework,
                    files,
                    packageUpdates,
                    build,
                    notes,
                };
            }
        }

        Record("update_solution", edits);
        await ReloadAsync();

        return new
        {
            succeeded = true,
            changedFiles = edits.Count,
            framework,
            files,
            packageUpdates,
            build,
            notes,
        };
    }

    // ── framework planning ────────────────────────────────────────────────────
    private sealed record FrameworkPlan(string TargetFramework, IReadOnlyList<FileEdit> Edits, List<object> Notes);

    private async Task<(FrameworkPlan? Plan, object? Error)> PlanFrameworkAsync(
        string? targetFramework, string[]? projectNames, bool updateGlobalJson, bool createGlobalJson,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return (null, new
            {
                error = "No C# solution could be auto-located. Call load_solution with an explicit path to the .sln/.slnx.",
            });

        var needSdkVersion = updateGlobalJson || createGlobalJson;
        var (target, sdkVersion, targetError) = await ResolveTargetAsync(targetFramework, needSdkVersion, timeoutSeconds);
        if (targetError is not null) return (null, targetError);

        var allProjects = solution.Projects.ToList();
        var selected = allProjects;
        if (projectNames is { Length: > 0 })
        {
            var missing = projectNames
                .Where(name => !allProjects.Any(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (missing.Count > 0)
                return (null, new
                {
                    error = $"Unknown project(s): {string.Join(", ", missing)}.",
                    knownProjects = allProjects.Select(project => project.Name),
                });

            selected = allProjects
                .Where(project => projectNames.Any(name => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var edits = new List<FileEdit>();
        var notes = new List<object>();

        foreach (var project in selected)
        {
            if (project.FilePath is null || !File.Exists(project.FilePath))
            {
                notes.Add(new { project = project.Name, reason = "the project file is not on disk; nothing to edit" });
                continue;
            }

            var text = await File.ReadAllTextAsync(project.FilePath, cancellationToken);
            if (!ProjectFiles.TryBumpTargetFrameworks(text, target!, out var updated, out var changes))
            {
                if (ProjectFiles.ReadTargetFrameworks(text).Count == 0)
                    notes.Add(new
                    {
                        project = project.Name,
                        reason = "no TargetFramework element in the project file (it may inherit one from Directory.Build.props, which is checked next)",
                    });
                continue;
            }

            edits.Add(new FileEdit(
                project.FilePath, "TargetFramework", project.Name,
                changes.Select(change => change.From).ToList(),
                changes.Select(change => change.To).ToList(),
                text, updated));
        }

        // Centralised properties: a Directory.Build.props can hold the TFM for every project.
        var searchDirectory = SolutionDirectory(solution)
            ?? selected.Select(project => project.FilePath).Where(path => path is not null)
                .Select(path => Path.GetDirectoryName(path!)).FirstOrDefault();
        if (searchDirectory is not null)
        {
            var propsPath = ProjectFiles.FindUpwards(searchDirectory, "Directory.Build.props");
            if (propsPath is not null)
            {
                var propsText = await File.ReadAllTextAsync(propsPath, cancellationToken);
                if (ProjectFiles.TryBumpTargetFrameworks(propsText, target!, out var propsUpdated, out var propsChanges))
                    edits.Add(new FileEdit(
                        propsPath, "TargetFramework", Path.GetFileName(propsPath),
                        propsChanges.Select(change => change.From).ToList(),
                        propsChanges.Select(change => change.To).ToList(),
                        propsText, propsUpdated));
            }
        }

        if ((updateGlobalJson || createGlobalJson) && searchDirectory is not null)
            edits.AddRange(PlanGlobalJson(searchDirectory, sdkVersion, updateGlobalJson, createGlobalJson, notes));

        return (new FrameworkPlan(target!, edits, notes), null);
    }

    private static IEnumerable<FileEdit> PlanGlobalJson(
        string searchDirectory, string? sdkVersion, bool updateGlobalJson, bool createGlobalJson, List<object> notes)
    {
        var globalJsonPath = ProjectFiles.FindUpwards(searchDirectory, "global.json");
        if (globalJsonPath is null)
        {
            if (!createGlobalJson) return [];

            if (sdkVersion is null)
            {
                notes.Add(new
                {
                    file = Path.Combine(searchDirectory, "global.json"),
                    reason = "no installed SDK matched the requested target, so no global.json was created",
                });
                return [];
            }

            return [new FileEdit(
                Path.Combine(searchDirectory, "global.json"), "global.json", "sdk.version",
                [], [sdkVersion], "", ProjectFiles.GlobalJsonTemplate(sdkVersion), CreatesFile: true)];
        }

        if (!updateGlobalJson) return [];

        var text = File.ReadAllText(globalJsonPath);
        if (sdkVersion is null)
        {
            notes.Add(new
            {
                file = globalJsonPath,
                reason = "no installed SDK matched the requested target, so the pinned version was left alone",
            });
            return [];
        }

        var previous = ProjectFiles.ReadGlobalJsonSdkVersion(text);
        if (!ProjectFiles.TryUpdateGlobalJsonSdkVersion(text, sdkVersion, out var updated))
        {
            notes.Add(new
            {
                file = globalJsonPath,
                reason = "global.json has no sdk.version to update; add one by hand if the SDK should be pinned",
            });
            return [];
        }

        if (string.Equals(previous, sdkVersion, StringComparison.OrdinalIgnoreCase)) return [];

        return [new FileEdit(
            globalJsonPath, "global.json", "sdk.version",
            previous is null ? Array.Empty<string>() : new[] { previous }, [sdkVersion], text, updated)];
    }

    private async Task<(string? Target, string? SdkVersion, object? Error)> ResolveTargetAsync(
        string? requested, bool needSdkVersion, int timeoutSeconds)
    {
        var isAuto = requested is null || string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase);

        if (!isAuto)
        {
            if (!ProjectFiles.TryNormalizeTargetFramework(requested, out var explicitTarget, out var normalizationError))
                return (null, null, new { error = normalizationError });

            // The value bump needs no CLI call unless an SDK version is about to be pinned.
            if (!needSdkVersion) return (explicitTarget, null, null);

            var (listed, output, _) = await DotnetAsync("--list-sdks", 60);
            return listed
                ? (explicitTarget, SdkOfBand(output, explicitTarget) ?? ProjectFiles.NewestSdkVersion(output), null)
                : (explicitTarget, null, null);
        }

        var (ok, autoOutput, cliError) = await DotnetAsync("--list-sdks", 60);
        if (!ok)
            return (null, null, new
            {
                error = cliError ?? "dotnet --list-sdks failed, so the target framework could not be inferred. " +
                                    "Pass targetFramework explicitly (e.g. 'net10.0').",
            });

        var newest = ProjectFiles.NewestSdkVersion(autoOutput);
        var auto = ProjectFiles.SdkTargetFromVersion(newest ?? "");
        if (auto is null)
            return (null, null, new
            {
                error = "No installed .NET SDK could be read from 'dotnet --list-sdks'. " +
                        "Pass targetFramework explicitly (e.g. 'net10.0').",
                output = ProcessRunner.Truncate(autoOutput, 2000),
            });

        return (auto, newest, null);
    }

    private static string? SdkOfBand(string listSdksOutput, string targetFramework)
    {
        var band = ProjectFiles.ParseDotNetTfm(targetFramework);
        if (band is null) return null;

        Version? best = null;
        string? bestText = null;
        foreach (Match match in Regex.Matches(listSdksOutput, @"(?m)^\s*(?<version>\d+\.\d+\.\d+[^\s\[\]]*)\s+\[").Cast<Match>())
        {
            var text = match.Groups["version"].Value;
            var core = text.Split('-')[0].Split('.');
            if (core.Length < 2 || !int.TryParse(core[0], out var major) || !int.TryParse(core[1], out var minor)) continue;
            if (major != band.Value.Major || minor != band.Value.Minor) continue;

            if (!Version.TryParse(text.Split('-')[0], out var version)) continue;
            if (best is null || version > best) { best = version; bestText = text; }
        }

        return bestText;
    }

    // ── package planning ──────────────────────────────────────────────────────
    private sealed class PendingEdit(string original)
    {
        public string Original { get; } = original;
        public string Updated { get; set; } = original;
        public List<(string Subject, string From, string To)> Items { get; } = [];
    }

    private async Task<(List<FileEdit> Edits, List<object> Updates, List<object> Notes, object? Error)> PlanPackagesAsync(
        string[]? projectNames, bool includePrerelease, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var edits = new List<FileEdit>();
        var updates = new List<object>();
        var notes = new List<object>();

        var (target, targetError) = await NuGetCli.ResolveTargetAsync(_workspace, null);
        if (targetError is not null) return (edits, updates, notes, targetError);

        var (packages, listError) = await NuGetCli.ListAsync(
            target!, "--outdated" + (includePrerelease ? " --include-prerelease" : ""), timeoutSeconds);
        if (listError is not null) return (edits, updates, notes, listError);

        var all = packages ?? [];

        // A floating version ("1.*") or a range ("[1.0,2.0)") is an intent, not a pin: replacing it
        // with today's latest would silently change that intent, so it is reported instead.
        static bool IsFloating(NuGetCli.Package package) =>
            package.Requested is { } requested &&
            (requested.Contains('*') || requested.Contains('[') || requested.Contains('('));

        foreach (var package in all.Where(package => !package.Transitive && IsFloating(package)))
            notes.Add(new
            {
                package = package.Id,
                project = package.Project,
                requested = package.Requested,
                reason = "the reference uses a floating version or a range; pinning it to today's latest would change the intent",
            });

        var outdated = all
            .Where(package => !package.Transitive && !IsFloating(package) && package.Latest is not null &&
                              !string.Equals(package.Latest, package.Resolved, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(package.Latest, package.Requested, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectNames is { Length: > 0 })
            outdated = outdated
                .Where(package => projectNames.Any(name => string.Equals(name, package.Project, StringComparison.OrdinalIgnoreCase)))
                .ToList();

        if (outdated.Count == 0) return (edits, updates, notes, null);

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        var searchDirectory = SolutionDirectory(solution ?? null)
            ?? Directory.GetCurrentDirectory();
        var propsPath = ProjectFiles.FindUpwards(searchDirectory, "Directory.Packages.props");
        var propsOriginal = propsPath is not null ? await File.ReadAllTextAsync(propsPath, cancellationToken) : null;

        var pending = new Dictionary<string, PendingEdit>(StringComparer.OrdinalIgnoreCase);
        var propsDesired = new Dictionary<string, (string From, string To)>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in outdated)
        {
            var latest = package.Latest!;
            var from = package.Resolved ?? package.Requested ?? "";

            // A Central Package Management row is the single place the version lives; edit it there
            // rather than failing on every project file that only carries a versionless reference.
            if (propsOriginal is not null &&
                ProjectFiles.TrySetPackageVersion(propsOriginal, package.Id, latest, out _, "PackageVersion"))
            {
                var to = propsDesired.TryGetValue(package.Id, out var known)
                    ? ProjectFiles.HigherVersion(known.To, latest)
                    : latest;
                propsDesired[package.Id] = (from, to);
                continue;
            }

            var csproj = solution?.Projects
                .FirstOrDefault(project => string.Equals(project.Name, package.Project, StringComparison.OrdinalIgnoreCase))
                ?.FilePath;

            if (csproj is null || !File.Exists(csproj))
            {
                notes.Add(new
                {
                    package = package.Id,
                    project = package.Project,
                    from,
                    to = latest,
                    reason = "the project file was not found in the loaded solution",
                });
                continue;
            }

            if (!pending.TryGetValue(csproj, out var staged))
            {
                staged = new PendingEdit(await File.ReadAllTextAsync(csproj, cancellationToken));
                pending[csproj] = staged;
            }

            if (ProjectFiles.TrySetPackageVersion(staged.Updated, package.Id, latest, out var updated))
            {
                staged.Updated = updated;
                staged.Items.Add((package.Id, from, latest));
                continue;
            }

            notes.Add(new
            {
                package = package.Id,
                project = package.Project,
                from,
                to = latest,
                reason = "the version is not written in the project file (Central Package Management or an MSBuild property); update it where it is defined",
            });
        }

        if (propsPath is not null && propsOriginal is not null && propsDesired.Count > 0)
        {
            var updated = propsOriginal;
            foreach (var (id, versions) in propsDesired)
                if (ProjectFiles.TrySetPackageVersion(updated, id, versions.To, out var next, "PackageVersion"))
                    updated = next;

            if (!string.Equals(updated, propsOriginal, StringComparison.Ordinal))
                edits.Add(new FileEdit(
                    propsPath, "package", Path.GetFileName(propsPath),
                    propsDesired.Values.Select(versions => versions.From).ToList(),
                    propsDesired.Values.Select(versions => versions.To).ToList(),
                    propsOriginal, updated));

            foreach (var (id, versions) in propsDesired)
                updates.Add(new { package = id, file = propsPath, from = versions.From, to = versions.To });
        }

        foreach (var (file, staged) in pending)
        {
            if (string.Equals(staged.Updated, staged.Original, StringComparison.Ordinal)) continue;

            edits.Add(new FileEdit(
                file, "package", Path.GetFileName(file),
                staged.Items.Select(item => item.From).ToList(),
                staged.Items.Select(item => item.To).ToList(),
                staged.Original, staged.Updated));

            foreach (var item in staged.Items)
                updates.Add(new { package = item.Subject, file, from = item.From, to = item.To });
        }

        return (edits, updates, notes, null);
    }

    // ── apply / gate / rollback ───────────────────────────────────────────────
    private sealed record FileEdit(
        string File, string Kind, string Subject,
        IReadOnlyList<string> From, IReadOnlyList<string> To,
        string Original, string Updated, bool CreatesFile = false)
    {
        public object ToPlan() => new
        {
            file = File,
            kind = Kind,
            subject = Subject,
            from = From,
            to = To,
            createsFile = CreatesFile,
        };
    }

    private static async Task ApplyAsync(IEnumerable<FileEdit> edits)
    {
        foreach (var edit in edits)
            await FileText.WriteAsync(edit.File, edit.Updated);
    }

    private static async Task RollbackAsync(IEnumerable<FileEdit> edits)
    {
        foreach (var edit in edits)
        {
            try
            {
                if (edit.CreatesFile)
                {
                    if (File.Exists(edit.File)) File.Delete(edit.File);
                }
                else
                {
                    await FileText.WriteAsync(edit.File, edit.Original);
                }
            }
            catch (IOException)
            {
                // best effort: the caller is told which files were meant to be restored
            }
        }
    }

    private void Record(string tool, IEnumerable<FileEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.CreatesFile) continue;
            try { _store.RecordEdit(tool, edit.File, edit.Kind, edit.Original, edit.Updated); }
            catch (Exception) { /* the history is an audit trail, not a precondition */ }
        }
    }

    private async Task<(bool Ok, object? Build)> GateAsync(int timeoutSeconds)
    {
        var result = await _build.RunAsync(null, "Debug", noCopyOutput: false, restore: true, timeoutSeconds, runAnalyzers: false);

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

    private async Task ReloadAsync()
    {
        if (_workspace.LoadedSolutionPath is not { } solutionPath) return;
        try { await _workspace.LoadSolutionAsync(solutionPath); } catch { /* files are on disk; the next tool reloads on demand */ }
    }

    private string? SolutionDirectory(Solution? solution)
    {
        var path = _workspace.LoadedSolutionPath ?? solution?.FilePath;
        return path is not null ? Path.GetDirectoryName(path) : null;
    }

    private static async Task<(bool Ok, string Output, string? Error)> DotnetAsync(string args, int timeoutSeconds)
    {
        try
        {
            var (exitCode, timedOut, stdout, stderr, _) =
                await ProcessRunner.RunAsync("dotnet", args, Directory.GetCurrentDirectory(), timeoutSeconds);

            if (timedOut)
                return (false, (stdout + "\n" + stderr).Trim(), $"dotnet {args} timed out after {timeoutSeconds}s.");

            return (exitCode == 0, (stdout + "\n" + stderr).Trim(), null);
        }
        catch (Exception ex)
        {
            return (false, "", $"Could not start the .NET CLI: {ex.Message}");
        }
    }
}
