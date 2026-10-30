using Snitch.Workspace;

namespace Snitch.Languages.Razor;

/// <summary>
/// Security audit of Razor pages: XSS sinks, CSRF gaps, credentials in markup, insecure URLs and
/// CSP weaknesses, each with a CWE id and a remediation.
/// </summary>
public interface IFindRazorVulnerabilitiesService
{
    Task<object> FindAsync(
        string? projectName,
        string? path,
        string minSeverity,
        string? cwe,
        int maxFindings);
}

public sealed class FindRazorVulnerabilitiesService : IFindRazorVulnerabilitiesService
{
    private const int MaxPages = 5000;

    private readonly IRoslynWorkspace _workspace;
    public FindRazorVulnerabilitiesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FindAsync(
        string? projectName = null,
        string? path = null,
        string minSeverity = "Low",
        string? cwe = null,
        int maxFindings = 200)
    {
        if (!RazorAnalysis.IsKnownSeverity(minSeverity))
            return new
            {
                error = $"Unknown severity '{minSeverity}'. Use one of: {string.Join(", ", RazorRuleCatalog.Severities)}.",
            };

        maxFindings = RazorAnalysis.Clamp(maxFindings, 200, 5000);
        var cweFilter = NormaliseCwe(cwe);

        var collection = await RazorAnalysis.CollectAsync(_workspace, projectName, path, MaxPages);
        if (collection.Error is not null)
            return new { error = collection.Error };

        var all = new List<RazorFinding>();
        var pagesAnalyzed = 0;

        foreach (var file in collection.Files)
        {
            var document = RazorAnalysis.TryRead(file, collection);
            if (document is null) continue;

            pagesAnalyzed++;
            all.AddRange(RazorRules.Run(document)
                .Where(finding => RazorRuleCatalog.IsSecurity(finding.Category)));
        }

        var minimum = RazorRuleCatalog.Rank(minSeverity);
        var filtered = all
            .Where(finding => RazorRuleCatalog.Rank(finding.Severity) >= minimum)
            .Where(finding => cweFilter is null ||
                              (finding.Cwe?.Contains(cweFilter, StringComparison.OrdinalIgnoreCase) ?? false));

        var sorted = RazorAnalysis.Sort(filtered);
        var returned = sorted.Take(maxFindings).ToList();

        return new
        {
            root = collection.Root,
            projectFilter = projectName ?? "all",
            pathFilter = path,
            minSeverity,
            cweFilter = cweFilter,
            pagesAnalyzed,
            pagesWithFindings = sorted.Select(finding => finding.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalFindings = sorted.Count,
            returnedFindings = returned.Count,
            truncated = sorted.Count > returned.Count,
            bySeverity = RazorAnalysis.CountBy(sorted, finding => finding.Severity),
            byCwe = RazorAnalysis.CountBy(
                sorted.Where(finding => finding.Cwe is not null), finding => finding.Cwe!),
            byRule = RazorAnalysis.CountBy(sorted, finding => finding.RuleId),
            skipped = collection.Skipped.Count > 0 ? collection.Skipped : null,
            unreadable = collection.Unreadable.Count > 0 ? collection.Unreadable : null,
            findings = returned.Select(RazorAnalysis.ToJson),
            notes = new[]
            {
                "HTML encoding protects text and attribute values, but not a <script> block, an inline handler or a javascript: URL — those are the sinks reported here.",
                "Heuristic: values that flow through partials, view models or client-side JavaScript are not traced. A clean result is not a proof of safety.",
            },
        };
    }

    private static string? NormaliseCwe(string? cwe)
    {
        if (string.IsNullOrWhiteSpace(cwe)) return null;

        var trimmed = cwe.Trim();
        if (int.TryParse(trimmed, out _))
            return "CWE-" + trimmed;
        return trimmed;
    }
}
