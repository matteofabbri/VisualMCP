using Snitch.Workspace;

namespace Snitch.Languages.Razor;

/// <summary>
/// Problems in Razor markup that are not security vulnerabilities: broken HTML, accessibility,
/// Razor misuse and maintainability. Security findings are reported by
/// <see cref="IFindRazorVulnerabilitiesService"/>.
/// </summary>
public interface IAnalyzeRazorPagesService
{
    Task<object> AnalyzeAsync(
        string? projectName,
        string? path,
        string? category,
        string? rule,
        string minSeverity,
        int maxFindings);
}

public sealed class AnalyzeRazorPagesService : IAnalyzeRazorPagesService
{
    private const int MaxPages = 5000;

    private readonly IRoslynWorkspace _workspace;
    public AnalyzeRazorPagesService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AnalyzeAsync(
        string? projectName = null,
        string? path = null,
        string? category = null,
        string? rule = null,
        string minSeverity = "Low",
        int maxFindings = 200)
    {
        if (!RazorAnalysis.IsKnownSeverity(minSeverity))
            return new
            {
                error = $"Unknown severity '{minSeverity}'. Use one of: {string.Join(", ", RazorRuleCatalog.Severities)}.",
            };

        maxFindings = RazorAnalysis.Clamp(maxFindings, 200, 5000);

        var collection = await RazorAnalysis.CollectAsync(_workspace, projectName, path, MaxPages);
        if (collection.Error is not null)
            return new { error = collection.Error };

        var all = new List<RazorFinding>();
        var pagesAnalyzed = 0;
        var partials = RazorAnalysis.CreatePartialResolver(collection);

        foreach (var file in collection.Files)
        {
            var document = RazorAnalysis.TryRead(file, collection);
            if (document is null) continue;

            pagesAnalyzed++;
            all.AddRange(RazorRules.Run(document, partials.For(Path.GetDirectoryName(document.FilePath)))
                .Where(finding => !RazorRuleCatalog.IsSecurity(finding.Category)));
        }

        var minimum = RazorRuleCatalog.Rank(minSeverity);
        var filtered = all
            .Where(finding => category is null ||
                              finding.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Where(finding => rule is null ||
                              finding.RuleId.Equals(rule, StringComparison.OrdinalIgnoreCase))
            .Where(finding => RazorRuleCatalog.Rank(finding.Severity) >= minimum);

        var sorted = RazorAnalysis.Sort(filtered);
        var returned = sorted.Take(maxFindings).ToList();

        return new
        {
            root = collection.Root,
            projectFilter = projectName ?? "all",
            pathFilter = path,
            categoryFilter = category ?? "all",
            ruleFilter = rule ?? "all",
            minSeverity,
            pagesAnalyzed,
            pagesWithFindings = sorted.Select(finding => finding.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalFindings = sorted.Count,
            returnedFindings = returned.Count,
            truncated = sorted.Count > returned.Count,
            bySeverity = RazorAnalysis.CountBy(sorted, finding => finding.Severity),
            byCategory = RazorAnalysis.CountBy(sorted, finding => finding.Category),
            byRule = RazorAnalysis.CountBy(sorted, finding => finding.RuleId),
            skipped = collection.Skipped.Count > 0 ? collection.Skipped : null,
            unreadable = collection.Unreadable.Count > 0 ? collection.Unreadable : null,
            findings = returned.Select(RazorAnalysis.ToJson),
            notes = new[]
            {
                "This tool returns markup, accessibility, Razor and maintainability problems. " +
                "Security findings (XSS, CSRF, secrets, CSP) -> find_razor_vulnerabilities.",
                "The analysis is heuristic: it scans markup and Razor expressions, it does not compile the page.",
            },
        };
    }
}
