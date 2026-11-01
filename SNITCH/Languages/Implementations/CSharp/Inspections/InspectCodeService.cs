using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>One inspection finding, already mapped onto Rider's vocabulary.</summary>
public sealed record InspectionFinding(
    string InspectionId,
    string Severity,
    string Message,
    string File,
    int Line,
    int Column,
    string Project,
    string Pack,
    string Category);

/// <summary>The outcome of one inspection run over the loaded solution.</summary>
public sealed record InspectionReport(
    string State,
    string Solution,
    long ElapsedMs,
    int Total,
    int Returned,
    bool Truncated,
    IReadOnlyList<InspectionFinding> Findings,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByInspection,
    IReadOnlyDictionary<string, int> ByPack,
    IReadOnlyList<string> Packs,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public interface IInspectCodeService
{
    /// <summary>Runs the built-in inspection packs over the solution (or one project / file).</summary>
    Task<InspectionReport> RunAsync(
        string? projectName,
        string? filePath,
        string packs,
        string minSeverity,
        string? ids,
        string? excludeIds,
        bool enableAll,
        int maxResults,
        int timeoutSeconds);
}

/// <summary>
/// The inspection engine behind <c>inspect_code</c>. Rider's own engine cannot be hosted outside
/// the IDE, so this runs the shipped Roslyn analyzer packs against the loaded solution's
/// compilations and reports their diagnostics in Rider's shape (severity, inspection id, file,
/// line). Unlike <see cref="Analysis.RunCodeAnalysisService"/> it does not need the inspected
/// project to reference any analyzer.
/// </summary>
public sealed class InspectCodeService : IInspectCodeService
{
    /// <summary>Upper bound on collected findings, so a rule storm cannot exhaust memory.</summary>
    private const int CollectionLimit = 50_000;

    private static readonly object CacheLock = new();

    /// <summary>
    /// Last report, keyed by the request and by the <see cref="Solution"/> *instance*. The engine
    /// replaces that instance whenever a document changes (load, or a refactoring write through
    /// TryUpdateDocument), so identity is an exact invalidation signal — no TTL guesswork.
    /// </summary>
    private (Solution Solution, string Key, InspectionReport Report)? _cached;

    private readonly IRoslynWorkspace _workspace;

    /// <summary>Optional on-disk store: keeps the last report across restarts of the server.</summary>
    private readonly IVisualMcpStore? _store;

    public InspectCodeService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<InspectionReport> RunAsync(
        string? projectName,
        string? filePath,
        string packs,
        string minSeverity,
        string? ids,
        string? excludeIds,
        bool enableAll,
        int maxResults,
        int timeoutSeconds)
    {
        var stopwatch = Stopwatch.StartNew();

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return Failed("", stopwatch, null,
                "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx.");

        var cacheKey = string.Join('|', projectName, filePath, packs, minSeverity, ids, excludeIds, enableAll, maxResults);
        lock (CacheLock)
        {
            if (_cached is { } cached && ReferenceEquals(cached.Solution, solution) && cached.Key == cacheKey)
                return cached.Report;
        }

        // Same report, but from a previous process: reuse it when no file changed on disk.
        var stamp = SolutionStamp(solution);
        var storeKey = "inspection:" + cacheKey;
        if (TryReadStoredReport(storeKey, stamp) is { } stored)
        {
            lock (CacheLock) _cached = (solution, cacheKey, stored);
            return stored;
        }

        var library = InspectionPackCatalog.Library;
        var selected = SelectPacks(library, packs, out var unknownPacks);        if (selected.Count == 0)
        {
            var known = string.Join(", ", InspectionPackCatalog.Definitions.Select(p => p.Id));
            return Failed(solution.FilePath ?? "", stopwatch, null,
                $"No inspection pack selected{(unknownPacks.Count > 0 ? $" (unknown: {string.Join(", ", unknownPacks)})" : "")}. " +
                $"Available: {known}." + Describe(library.Warnings));
        }

        var analyzers = selected.SelectMany(p => p.Analyzers).ToImmutableArray();
        var rules = RuleIds(analyzers);
        var dormantRules = DormantRules(analyzers);
        var packByRule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in selected)
            foreach (var rule in RuleIds(pack.Analyzers))
                packByRule.TryAdd(rule, pack.Pack.Id);

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return Failed(solution.FilePath ?? "", stopwatch, selected,
                $"Project '{projectName}' not found.");

        var include = ParseFilter(ids);
        var exclude = ParseFilter(excludeIds);
        var minimum = ParseSeverity(minSeverity);
        var fileFilter = ResolvePath(filePath);

        var findings = new List<InspectionFinding>();
        var warnings = new List<string>(library.Warnings);
        var timedOut = false;
        var crashed = 0;
        var capped = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 900)));

        foreach (var project in projects)
        {
            try
            {
                // A pack contributes the analyzers that read the project's language: the .NET analyzers
                // ship a C# and a VB set, the house rules have one implementation each, and the
                // C#-only packs (Roslynator, StyleCop, AsyncFixer, Meziantou, the ReSharper-shaped
                // ones) inspect no VB project at all. Saying so once is better than a silent 0.
                var projectAnalyzers = selected
                    .SelectMany(pack => pack.AnalyzersFor(project.Language))
                    .Distinct()
                    .ToImmutableArray();

                if (projectAnalyzers.IsEmpty)
                {
                    var language = project.Language == LanguageNames.VisualBasic ? "Visual Basic" : "C#";
                    foreach (var pack in selected.Where(pack => pack.AnalyzersFor(project.Language).IsEmpty))
                        warnings.Add($"{pack.Pack.Id}: no {language} analyzer " +
                                     $"(the pack is {string.Join("/", pack.Languages)} only).");

                    continue;
                }

                var compilation = await project.GetCompilationAsync(cts.Token);
                if (compilation is null) continue;

                if (enableAll)
                    compilation = EnableAllRules(compilation, dormantRules);

                var withAnalyzers = compilation.WithAnalyzers(projectAnalyzers, project.AnalyzerOptions);
                var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cts.Token);

                foreach (var diagnostic in diagnostics)
                {
                    // AD0001 = an analyzer threw; it is a defect in the pack, not a finding.
                    if (diagnostic.Id == "AD0001" || diagnostic.Id == "AD0002") { crashed++; continue; }
                    if (diagnostic.IsSuppressed) continue;
                    if (diagnostic.Severity < minimum) continue;
                    if (diagnostic.Location is not { IsInSource: true } location) continue;
                    if (!Matches(diagnostic.Id, include, exclude)) continue;

                    var span = location.GetLineSpan();
                    if (fileFilter is not null &&
                        !span.Path.Equals(fileFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (findings.Count >= CollectionLimit) { capped = true; break; }

                    findings.Add(new InspectionFinding(
                        diagnostic.Id,
                        RiderSeverity(diagnostic.Severity),
                        diagnostic.GetMessage(),
                        span.Path,
                        span.StartLinePosition.Line + 1,
                        span.StartLinePosition.Character + 1,
                        project.Name,
                        packByRule.TryGetValue(diagnostic.Id, out var packId) ? packId : "",
                        diagnostic.Descriptor.Category));
                }
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                break;
            }
            catch (Exception ex)
            {
                warnings.Add($"{project.Name}: {ex.GetType().Name}: {ex.Message}");
            }

            if (capped) break;
        }

        var ordered = findings
            .OrderByDescending(f => Rank(f.Severity))
            .ThenBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .ToList();

        var limit = Math.Clamp(maxResults, 1, 5000);
        var returned = ordered.Take(limit).ToList();

        if (crashed > 0)
            warnings.Add($"{crashed} analyzer crash report(s) (AD0001) were ignored.");

        var report = new InspectionReport(
            timedOut ? "timeout" : "completed",
            solution.FilePath ?? "",
            stopwatch.ElapsedMilliseconds,
            ordered.Count,
            returned.Count,
            ordered.Count > returned.Count || capped || timedOut,
            returned,
            ordered.GroupBy(f => f.Severity).OrderByDescending(g => Rank(g.Key)).ToDictionary(g => g.Key, g => g.Count()),
            ordered.GroupBy(f => f.InspectionId).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            ordered.GroupBy(f => f.Pack).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            selected.Select(p => p.Pack.Id).ToList(),
            Distinct(warnings),
            timedOut
                ? $"The inspection hit the {Math.Clamp(timeoutSeconds, 5, 900)}s timeout; results are partial. " +
                  "Restrict with projectName/filePath, raise timeoutSeconds, or narrow the packs."
                : null);

        lock (CacheLock) _cached = (solution, cacheKey, report);
        Store(report, storeKey, stamp);
        return report;
    }

    private InspectionReport? TryReadStoredReport(string key, string stamp)
    {
        if (_store?.Get(key) is not { } entry) return null;
        if (!string.Equals(entry.Stamp, stamp, StringComparison.Ordinal)) return null;

        try
        {
            return JsonSerializer.Deserialize<InspectionReport>(entry.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Store(InspectionReport report, string key, string stamp)
    {
        if (_store is null) return;

        try
        {
            _store.Set(key, JsonSerializer.Serialize(report), stamp);
        }
        catch (NotSupportedException)
        {
            // The report shape is not serializable: skip the persistent cache, keep the in-memory one.
        }
    }

    /// <summary>
    /// Fingerprint of the solution's files: path plus last write time. A stored report is reused only
    /// while this matches, so an edit on disk (by anyone) makes the next run re-analyze.
    /// </summary>
    private static string SolutionStamp(Solution solution)
    {
        var builder = new StringBuilder();

        foreach (var project in solution.Projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            foreach (var document in project.Documents
                         .Where(document => document.FilePath is not null)
                         .OrderBy(document => document.FilePath, StringComparer.Ordinal))
            {
                builder.Append(document.FilePath).Append('|');
                try
                {
                    builder.Append(File.GetLastWriteTimeUtc(document.FilePath!).Ticks);
                }
                catch (IOException)
                {
                    builder.Append('0');
                }

                builder.Append(';');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static List<LoadedInspectionPack> SelectPacks(
        InspectionPackLibrary library, string? spec, out List<string> unknown)
    {
        unknown = new List<string>();

        if (string.IsNullOrWhiteSpace(spec))
        {
            var defaults = InspectionPackCatalog.DefaultPacks
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return library.Packs
                .Where(p => defaults.Contains(p.Pack.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (spec.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return library.Packs.ToList();

        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = new List<LoadedInspectionPack>();

        foreach (var id in wanted)
        {
            var match = library.Packs.FirstOrDefault(p =>
                p.Pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (match is null) unknown.Add(id);
            else if (!selected.Contains(match)) selected.Add(match);
        }

        return selected;
    }

    private static InspectionReport Failed(
        string solution, Stopwatch stopwatch, IReadOnlyList<LoadedInspectionPack>? packs, string error) =>
        new("failed", solution, stopwatch.ElapsedMilliseconds, 0, 0, false,
            Array.Empty<InspectionFinding>(),
            new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>(),
            packs is null ? new List<string>() : packs.Select(p => p.Pack.Id).ToList(),
            Array.Empty<string>(), error);

    private static string Describe(IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? "" : " Loader warnings: " + string.Join(" | ", warnings);

    private static ImmutableArray<string> RuleIds(ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var analyzer in analyzers)
        {
            try
            {
                builder.AddRange(analyzer.SupportedDiagnostics.Select(d => d.Id));
            }
            catch
            {
                // A pack that cannot enumerate its rules simply contributes none.
            }
        }

        return builder.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    /// <summary>
    /// Rules a pack ships disabled (Roslynator disables almost everything, as do many CA/MA ones).
    /// The map keeps each rule's own designed severity, so enabling them restores what the pack
    /// author intended instead of inventing one.
    /// </summary>
    internal static ImmutableDictionary<string, ReportDiagnostic> DormantRules(
        ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        var dormant = new Dictionary<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);

        foreach (var analyzer in analyzers)
        {
            try
            {
                foreach (var descriptor in analyzer.SupportedDiagnostics)
                {
                    if (descriptor.IsEnabledByDefault) continue;

                    dormant.TryAdd(descriptor.Id, ToReportDiagnostic(descriptor.DefaultSeverity));
                }
            }
            catch
            {
                // A pack that cannot enumerate its rules contributes none.
            }
        }

        return dormant.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static ReportDiagnostic ToReportDiagnostic(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => ReportDiagnostic.Error,
        DiagnosticSeverity.Warning => ReportDiagnostic.Warn,
        DiagnosticSeverity.Info => ReportDiagnostic.Info,
        _ => ReportDiagnostic.Hidden,
    };

    /// <summary>
    /// Enables the rules the packs keep off, at their designed severity, unless the inspected
    /// project configured that id itself (csproj NoWarn / ruleset stays authoritative).
    /// </summary>
    internal static Compilation EnableAllRules(
        Compilation compilation, ImmutableDictionary<string, ReportDiagnostic> dormantRules)
    {
        var options = compilation.Options;
        var overrides = new Dictionary<string, ReportDiagnostic>(options.SpecificDiagnosticOptions);
        var changed = false;

        foreach (var (id, severity) in dormantRules)
        {
            if (overrides.ContainsKey(id)) continue;

            overrides[id] = severity;
            changed = true;
        }

        return changed ? compilation.WithOptions(options.WithSpecificDiagnosticOptions(overrides)) : compilation;
    }

    private static string[]? ParseFilter(string? spec) =>
        string.IsNullOrWhiteSpace(spec)
            ? null
            : spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string id, string[]? include, string[]? exclude)
    {
        if (exclude is not null && MatchesAny(id, exclude)) return false;
        if (include is null || include.Length == 0) return true;

        return MatchesAny(id, include);
    }

    private static bool MatchesAny(string id, string[] patterns) =>
        patterns.Any(pattern => pattern.EndsWith('*')
            ? id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : id.Equals(pattern, StringComparison.OrdinalIgnoreCase));

    private static string? ResolvePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Distinct(List<string> warnings) =>
        warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static DiagnosticSeverity ParseSeverity(string? severity) =>
        severity?.Trim().ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "info" or "weak_warning" or "suggestion" => DiagnosticSeverity.Info,
            "hidden" or "hint" => DiagnosticSeverity.Hidden,
            _ => DiagnosticSeverity.Warning,
        };

    /// <summary>Rider's severity vocabulary, so the output reads like the IDE's inspection report.</summary>
    private static string RiderSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "ERROR",
        DiagnosticSeverity.Warning => "WARNING",
        DiagnosticSeverity.Info => "WEAK_WARNING",
        _ => "HINT",
    };

    private static int Rank(string severity) => severity switch
    {
        "ERROR" => 4,
        "WARNING" => 3,
        "WEAK_WARNING" => 2,
        _ => 1,
    };
}