using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>
/// The XAML misuse pass: duplicate names and keys, a missing or unresolvable code-behind type, an
/// event handler no method backs, a resource key nothing defines, a template without its type.
/// Markup-only by design: bindings and styles are not compiled, and the notes say so.
/// </summary>
public interface IAnalyzeXamlService
{
    Task<object> AnalyzeAsync(string? projectName, string? path, string? category, string? rule,
        string minSeverity, int maxFindings);
}

public sealed class AnalyzeXamlService : IAnalyzeXamlService
{
    private readonly IRoslynWorkspace _workspace;
    public AnalyzeXamlService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AnalyzeAsync(string? projectName = null, string? path = null, string? category = null,
        string? rule = null, string minSeverity = "Low", int maxFindings = 200)
    {
        if (!XamlRuleCatalog.Severities.Contains(minSeverity, StringComparer.OrdinalIgnoreCase))
            return new
            {
                error = $"Unknown severity '{minSeverity}'. Use one of: {string.Join(", ", XamlRuleCatalog.Severities)}.",
            };

        maxFindings = XamlAnalysis.Clamp(maxFindings, 200, 5000);

        var collection = await XamlAnalysis.CollectAsync(_workspace, projectName, path, 5000);
        if (collection.Error is not null) return new { error = collection.Error };

        var documents = new List<XamlDocument>();
        var findings = new List<XamlFinding>();

        foreach (var file in collection.Files)
        {
            var document = XamlAnalysis.TryRead(file, collection, findings);
            if (document is not null) documents.Add(document);
        }

        var keysByProject = documents
            .GroupBy(document => document.Project ?? string.Empty)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group.SelectMany(document => document.Keys.Select(key => key.Key))
                    .ToHashSet(StringComparer.Ordinal));

        var solution = await _workspace.EnsureSolutionLoadedAsync();
        var typeCache = new Dictionary<string, XamlTypeFacts?>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            XamlTypeFacts? codeBehind = null;
            var resolvable = false;

            if (!string.IsNullOrWhiteSpace(document.ClassName))
            {
                resolvable = solution is not null;
                var cacheKey = (document.Project ?? string.Empty) + "|" + document.ClassName;
                if (!typeCache.TryGetValue(cacheKey, out codeBehind))
                {
                    codeBehind = await XamlAnalysis.ResolveTypeAsync(solution, document.Project, document.ClassName!);
                    typeCache[cacheKey] = codeBehind;
                }
            }

            findings.AddRange(XamlRules.Run(document, codeBehind, resolvable,
                keysByProject.TryGetValue(document.Project ?? string.Empty, out var keys) ? keys : new HashSet<string>()));
        }

        var minimum = XamlRuleCatalog.Rank(minSeverity);
        var filtered = findings
            .Where(finding => category is null || finding.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Where(finding => rule is null || finding.RuleId.Equals(rule, StringComparison.OrdinalIgnoreCase))
            .Where(finding => XamlRuleCatalog.Rank(finding.Severity) >= minimum);

        var sorted = XamlAnalysis.Sort(filtered);
        var returned = sorted.Take(maxFindings).ToList();

        return new
        {
            root = collection.Root,
            projectFilter = projectName ?? "all",
            pathFilter = path,
            categoryFilter = category ?? "all",
            ruleFilter = rule ?? "all",
            minSeverity,
            filesAnalyzed = documents.Count,
            filesWithFindings = sorted.Select(finding => finding.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            totalFindings = sorted.Count,
            returnedFindings = returned.Count,
            truncated = sorted.Count > returned.Count,
            bySeverity = XamlAnalysis.CountBy(sorted, finding => finding.Severity),
            byCategory = XamlAnalysis.CountBy(sorted, finding => finding.Category),
            byRule = XamlAnalysis.CountBy(sorted, finding => finding.RuleId),
            skipped = collection.Skipped.Count > 0 ? collection.Skipped : null,
            unreadable = collection.Unreadable.Count > 0 ? collection.Unreadable : null,
            findings = returned.Select(XamlAnalysis.ToJson),
            notes = new[]
            {
                "Heuristic markup scan: it reads the XML and resolves the code-behind type through Roslyn. " +
                "Bindings, styles and resources are not compiled, so a clean result is not a proof of correctness.",
                "XAML0005 only fires when the project defines at least one x:Key: a key from a referenced " +
                "assembly's theme dictionary is invisible here.",
            },
        };
    }
}
