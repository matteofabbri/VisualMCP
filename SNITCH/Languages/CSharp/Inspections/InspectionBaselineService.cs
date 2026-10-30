using System.Text.Json;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// A baseline for <c>inspect_code</c>: the findings a codebase already has, stored once, so a later run
/// can answer "what did this change introduce?" instead of "here are 4,000 findings". The fingerprint
/// is the inspection id plus the file and the message — the line is deliberately not part of it, or
/// every edit above a finding would report the whole file as new.
/// </summary>
public interface IInspectionBaselineService
{
    Task<object> Run(
        string action,
        string? projectName,
        string packs,
        string minSeverity,
        string? ids,
        string? excludeIds,
        int maxResults,
        int timeoutSeconds);
}

public sealed class InspectionBaselineService : IInspectionBaselineService
{
    private const string KeyPrefix = "inspection-baseline:";

    private readonly IInspectCodeService _inspections;
    private readonly IVisualMcpStore _store;
    private readonly IRoslynWorkspace _workspace;

    public InspectionBaselineService(
        IInspectCodeService inspections, IVisualMcpStore store, IRoslynWorkspace workspace)
    {
        _inspections = inspections;
        _store = store;
        _workspace = workspace;
    }

    private sealed record Baseline(
        string CreatedAt,
        string Project,
        string Packs,
        int Count,
        IReadOnlyList<string> Findings,
        IReadOnlyDictionary<string, int> ByInspection);

    public async Task<object> Run(
        string action,
        string? projectName,
        string packs,
        string minSeverity,
        string? ids,
        string? excludeIds,
        int maxResults,
        int timeoutSeconds)
    {
        var verb = (action ?? "check").Trim().ToLowerInvariant();
        var key = KeyPrefix + (projectName ?? "*");

        switch (verb)
        {
            case "show":
            {
                var stored = Load(key);
                if (stored is null)
                    return new { action = verb, note = $"No baseline stored for '{projectName ?? "the solution"}'. Run action=create first." };

                return new
                {
                    action = verb,
                    project = stored.Project,
                    createdAt = stored.CreatedAt,
                    count = stored.Count,
                    byInspection = stored.ByInspection,
                };
            }

            case "clear":
                return new { action = verb, removed = _store.Remove(key) };

            case "create":
            case "check":
                break;

            default:
                return new { error = "action must be create, check, show or clear." };
        }

        // A check needs a baseline before anything else: running the packs for thirty seconds only to
        // discover there is nothing to compare against would waste the one thing the tool exists to save.
        Baseline? existing = null;
        if (verb == "check")
        {
            existing = Load(key);
            if (existing is null)
                return new
                {
                    error = $"No baseline stored for '{projectName ?? "the solution"}'; run action=create first.",
                };
        }

        var report = await _inspections.RunAsync(
            projectName, null, packs, minSeverity, ids, excludeIds, enableAll: true,
            maxResults <= 0 ? 5000 : maxResults, timeoutSeconds <= 0 ? 120 : timeoutSeconds);

        if (report.State == "failed")
            return new { error = report.Error ?? "The inspection run failed.", packs = report.Packs };

        var current = Fingerprints(report.Findings);

        if (verb == "create")
        {
            var baseline = new Baseline(
                DateTimeOffset.UtcNow.ToString("O"),
                projectName ?? "*",
                string.Join(",", report.Packs),
                current.Count,
                current,
                report.ByInspection.ToDictionary(pair => pair.Key, pair => pair.Value));

            _store.Set(key, JsonSerializer.Serialize(baseline), baseline.CreatedAt);

            return new
            {
                action = verb,
                project = baseline.Project,
                stored = current.Count,
                bySeverity = report.BySeverity,
                byInspection = report.ByInspection,
                truncated = report.Truncated,
                note = report.Truncated
                    ? "The run was truncated: raise maxResults so the baseline holds every finding."
                    : "Baseline stored. Run action=check after a change to see only what is new.",
            };
        }

        var (added, fixedFindings) = CompareFindings(existing!.Findings, current);
        var addedSet = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);

        return new
        {
            action = verb,
            project = existing.Project,
            baselineAt = existing.CreatedAt,
            baseline = existing.Count,
            current = current.Count,
            newFindings = added.Count,
            fixedFindings = fixedFindings.Count,
            hasRegression = added.Count > 0,
            findings = report.Findings
                .Where(finding => addedSet.Contains(Fingerprint(finding)))
                .Take(maxResults <= 0 ? 200 : maxResults)
                .Select(finding => new
                {
                    id = finding.InspectionId,
                    severity = finding.Severity,
                    @file = finding.File,
                    line = finding.Line,
                    message = finding.Message,
                    project = finding.Project,
                })
                .ToList(),
            note = added.Count == 0
                ? "No new finding: the change did not introduce anything this baseline did not already have."
                : $"{added.Count} new finding(s) — the regression a CI gate should fail on.",
        };
    }

    /// <summary>
    /// The baseline comparison: what is in <paramref name="current"/> and not in
    /// <paramref name="baseline"/> (new), and the reverse (fixed). Case-insensitive, because the
    /// fingerprint is a path plus a message and Windows paths are.
    /// </summary>
    public static (List<string> Added, List<string> Fixed) CompareFindings(
        IEnumerable<string> baseline, IEnumerable<string> current)
    {
        var baselineSet = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
        var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);

        return (
            currentSet.Except(baselineSet, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList(),
            baselineSet.Except(currentSet, StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private List<string> Fingerprints(IEnumerable<InspectionFinding> findings) =>
        findings.Select(Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private string Fingerprint(InspectionFinding finding) =>
        finding.InspectionId + "|" + Relative(finding.File) + "|" + finding.Message;

    /// <summary>
    /// The path as the baseline should remember it: relative to the solution, so the store keeps
    /// working when the repository moves and a temp path does not leak into a fingerprint.
    /// </summary>
    private string Relative(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return file;

        var root = _workspace.LoadedSolutionPath;
        if (string.IsNullOrWhiteSpace(root)) return Path.GetFileName(file);

        try
        {
            return Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(root))!, Path.GetFullPath(file))
                .Replace('\\', '/');
        }
        catch (Exception)
        {
            return Path.GetFileName(file);
        }
    }

    private Baseline? Load(string key)
    {
        var entry = _store.Get(key);
        if (entry is null) return null;

        try
        {
            return JsonSerializer.Deserialize<Baseline>(entry.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
