using Snitch.Workspace;

namespace Snitch.Languages.WebMarkup;

/// <summary>Discovery and orchestration over the solution's HTML pages and stylesheets.</summary>
public interface IMarkupService
{
    /// <summary>What the markup files are: element, id and rule counts per file.</summary>
    Task<object> InventoryAsync(string? path, string kind, int maxFiles);

    /// <summary>The findings of the rule set, filtered by severity and rule id.</summary>
    Task<object> AnalyzeAsync(
        string? path, string kind, string minSeverity, string? rule, int maxFiles, int maxFindings);
}

public sealed class MarkupService : IMarkupService
{
    private static readonly string[] ExcludedDirectories =
    [
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "dist", "build", "out",
        "coverage", ".next", ".nuxt", "lib",
    ];

    private readonly IRoslynWorkspace _workspace;

    public MarkupService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InventoryAsync(string? path, string kind, int maxFiles)
    {
        var (normalizedKind, error) = Normalize(kind);
        if (error is not null) return new { error };

        maxFiles = Math.Clamp(maxFiles, 1, 20_000);
        var (root, files, truncated) = Discover(path, normalizedKind, maxFiles);

        var entries = new List<object>();
        var totalFindings = 0;

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;

            totalFindings += analysis.Findings.Count;
            entries.Add(new
            {
                file = Relative(root, file),
                kind = analysis.Kind,
                lines = analysis.Lines,
                elements = analysis.Elements,
                ids = analysis.Ids.Count,
                duplicateIds = analysis.Ids.GroupBy(id => id, StringComparer.Ordinal).Count(group => group.Count() > 1),
                classes = analysis.Nodes?.SelectMany(node => node.Classes)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
                selectors = analysis.Selectors?.Count ?? 0,
                findings = analysis.Findings.Count,
            });
        }

        return new
        {
            root,
            kind = normalizedKind,
            fileCount = entries.Count,
            truncated,
            maxFiles,
            totalFindings,
            files = entries,
            hint = "analyze_markup lists the findings themselves; markup_files only says what is there.",
        };
    }

    public async Task<object> AnalyzeAsync(
        string? path, string kind, string minSeverity, string? rule, int maxFiles, int maxFindings)
    {
        var (normalizedKind, error) = Normalize(kind);
        if (error is not null) return new { error };

        var severity = (minSeverity ?? "info").Trim().ToLowerInvariant();
        if (severity is not ("info" or "warning" or "error"))
            return new { error = "minSeverity must be info, warning or error." };

        var ruleId = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim().ToUpperInvariant();
        if (ruleId is not null && !MarkupAnalyzer.Rules.ContainsKey(ruleId))
            return new
            {
                error = $"Unknown rule '{rule}'.",
                rules = MarkupAnalyzer.Rules.Select(pair => new { id = pair.Key, proves = pair.Value }).ToList(),
            };

        maxFiles = Math.Clamp(maxFiles, 1, 20_000);
        maxFindings = Math.Clamp(maxFindings, 1, 5_000);

        var (root, files, truncated) = Discover(path, normalizedKind, maxFiles);
        var findings = new List<MarkupFinding>();
        var analyses = new List<(string File, MarkupAnalysis Analysis)>();
        var analyzed = 0;

        void AddIfSelected(MarkupFinding finding)
        {
            if (Rank(finding.Severity) < Rank(severity)) return;
            if (ruleId is not null && finding.RuleId != ruleId) return;
            findings.Add(finding);
        }

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;
            analyzed++;

            var relative = Relative(root, file);
            analyses.Add((relative, analysis));
            foreach (var finding in analysis.Findings)
                AddIfSelected(finding with { File = relative });
        }

        var notes = new List<string>
        {
            "A structural scan of tags and rules, not a renderer: it does not run a browser and cannot tell " +
            "whether a stylesheet is actually used.",
        };

        var pages = analyses.Where(entry => entry.Analysis.Kind == "html").ToList();

        // One element index PER PAGE: a parent index and a sibling index are positions in that page's own
        // list, so merging the pages would let one page's <div> parent another page's <span>.
        var pageNodes = pages
            .Select(entry => (IReadOnlyList<MarkupNode>)(entry.Analysis.Nodes ?? []))
            .ToList();

        // A <style> block inside a page is a stylesheet too: its selectors and classes are attached to the
        // page's analysis, so they take part in the same match and the finding keeps the page's file.
        var selectorSources = analyses
            .SelectMany(entry => (entry.Analysis.Selectors ?? []).Select(selector => (entry.File, Selector: selector)))
            .ToList();

        if (pages.Count > 0 && selectorSources.Count > 0)
        {
            notes.Add("CSS0009 resolves a selector against the tag/class/id/attribute index of the scanned pages " +
                      "(<style> blocks included): a compound part with its classes, id, attribute conditions and " +
                      "structural pseudo-classes (:root, :first-child, :last-child, :only-child, :empty), joined " +
                      "by descendant, child, adjacent-sibling and general-sibling combinators. A selector whose " +
                      "meaning only a browser can decide (:hover, :checked, :nth-child(), :has()) is indexed but " +
                      "never reported; CSS0010 resolves specificity and source order for a declaration that is " +
                      "overridden on every element it matches.");

            foreach (var (file, selector) in selectorSources)
            {
                // A dynamic pseudo-class is not a mismatch: the element may well be there in the state the
                // stylesheet describes, and a finding here would be a false positive.
                if (!selector.Checkable) continue;
                if (pageNodes.Any(nodes => nodes.Any(node => MarkupAnalyzer.Matches(nodes, node, selector)))) continue;
                AddIfSelected(UnmatchedSelector(file, selector));
            }

            var cssRules = analyses
                .SelectMany(entry => (entry.Analysis.Rules ?? []).Select(rule => (entry.File, Rule: rule)))
                .ToList();

            if (cssRules.Count > 0)
            {
                var elementTotal = pageNodes.Sum(nodes => nodes.Count);
                var declarationTotal = cssRules.Sum(entry => entry.Rule.Declarations.Count);
                if ((long)declarationTotal * elementTotal > 400_000)
                {
                    notes.Add($"CSS0010 was skipped: the cascade would compare {declarationTotal} declarations " +
                              $"against {elementTotal} elements.");
                }
                else
                {
                    foreach (var finding in DeadDeclarations(cssRules, pageNodes))
                        AddIfSelected(finding);

                    notes.Add("CSS0010 resolves a declaration's specificity (ids, then classes/attributes/" +
                              "pseudo-classes, then tags/pseudo-elements) and the source order within a file; " +
                              "equal specificity across two different files is treated as unknown, because the " +
                              "link order is not in the scan.");
                }
            }

            var defined = analyses
                .SelectMany(entry => entry.Analysis.Classes ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (file, analysis) in pages)
            {
                foreach (var node in analysis.Nodes ?? [])
                {
                    foreach (var className in node.Classes)
                    {
                        if (defined.Contains(className)) continue;
                        if (!reported.Add($"{file}|{node.Line}|{className}")) continue;

                        AddIfSelected(new MarkupFinding("HTML0015", "info", "Maintainability",
                            $"The class '{className}' is used here and defined in no scanned stylesheet.", file,
                            node.Line, node.Column, $"class=\"{className}\"",
                            "Remove the class, or write the rule it was meant to match. A stylesheet loaded from a " +
                            "CDN or a utility framework is outside this scan and makes the finding a false positive."));
                    }
                }
            }
        }
        else if (pages.Count == 0)
        {
            notes.Add("Selector matching was skipped: no HTML page is in scope.");
        }
        else
        {
            notes.Add("Selector matching was skipped: no stylesheet is in scope (no .css/.scss file and no <style> " +
                      "block), so CSS0009 and HTML0015 cannot be judged.");
        }

        var shown = findings.Take(maxFindings).ToList();
        return new
        {
            root,
            kind = normalizedKind,
            filesAnalyzed = analyzed,
            filesTruncated = truncated,
            minSeverity = severity,
            rule = ruleId,
            findingCount = findings.Count,
            counts = new
            {
                error = findings.Count(finding => finding.Severity == "error"),
                warning = findings.Count(finding => finding.Severity == "warning"),
                info = findings.Count(finding => finding.Severity == "info"),
            },
            byRule = findings.GroupBy(finding => finding.RuleId)
                .OrderByDescending(group => group.Count())
                .ToDictionary(group => group.Key, group => group.Count()),
            findings = shown,
            truncated = findings.Count > shown.Count,
            note = string.Join(" ", notes),
        };
    }

    private static MarkupFinding UnmatchedSelector(string file, CssSimpleSelector selector) =>
        new("CSS0009", "info", "Maintainability",
            $"The selector '{selector.Text}' matches no element (tag, class, id, attribute) in the scanned pages.",
            file, selector.Line, selector.Column, selector.Text,
            "Remove the rule, or check the spelling: a typo here is a style that never applies.");

    /// <summary>
    /// A declaration is dead when every element it applies to is won by another rule that sets the same
    /// property — higher specificity, <c>!important</c>, or the same specificity and a later rule in the
    /// same file. Equal specificity across files is unknown (the link order is not in the scan), so it does
    /// not make a declaration dead.
    /// </summary>
    private static IEnumerable<MarkupFinding> DeadDeclarations(
        List<(string File, CssRule Rule)> rules, List<IReadOnlyList<MarkupNode>> pages)
    {
        foreach (var (file, rule) in rules)
        {
            if (!rule.Selector.Checkable) continue;

            var matched = new List<(IReadOnlyList<MarkupNode> Nodes, MarkupNode Node)>();
            foreach (var page in pages)
                foreach (var node in page)
                    if (MarkupAnalyzer.Matches(page, node, rule.Selector))
                        matched.Add((page, node));

            if (matched.Count == 0) continue;

            foreach (var declaration in rule.Declarations)
            {
                if (!matched.All(entry => Overridden(rules, rule, file, declaration, entry.Nodes, entry.Node)))
                    continue;

                yield return new MarkupFinding("CSS0010", "info", "Maintainability",
                    $"The declaration '{declaration.Property}: {declaration.Value}' in '{rule.Selector.Text}' is " +
                    $"overridden on every one of the {matched.Count} element(s) it matches.",
                    file, declaration.Line, declaration.Column,
                    $"{rule.Selector.Text} {{ {declaration.Property}: {declaration.Value} }}",
                    "Remove it, or raise its specificity: no element ever renders this value.");
            }
        }
    }

    private static bool Overridden(List<(string File, CssRule Rule)> rules, CssRule rule, string file,
        CssDeclaration declaration, IReadOnlyList<MarkupNode> page, MarkupNode node)
    {
        foreach (var (otherFile, other) in rules)
        {
            if (ReferenceEquals(other, rule)) continue;
            if (!other.Selector.Checkable) continue;
            if (!MarkupAnalyzer.Matches(page, node, other.Selector)) continue;

            var winner = other.Declarations.LastOrDefault(candidate => candidate.Property == declaration.Property);
            if (winner is null) continue;

            if (declaration.Important && !winner.Important) continue;
            if (!declaration.Important && winner.Important) return true;
            if (other.Specificity > rule.Specificity) return true;
            if (other.Specificity == rule.Specificity &&
                string.Equals(otherFile, file, StringComparison.OrdinalIgnoreCase) &&
                other.Order > rule.Order)
                return true;
        }

        return false;
    }

    private static (string Kind, string? Error) Normalize(string kind)
    {
        var normalized = (kind ?? "both").Trim().ToLowerInvariant();
        return normalized is "html" or "css" or "both"
            ? (normalized, null)
            : (normalized, "kind must be html, css or both.");
    }

    private async Task<MarkupAnalysis?> AnalyzeFileAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 4_000_000) return null;
            var text = await File.ReadAllTextAsync(file);

            return MarkupAnalyzer.IsHtml(file)
                ? MarkupAnalyzer.AnalyzeHtml(text, file)
                : MarkupAnalyzer.AnalyzeCss(text, file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private (string Root, List<string> Files, bool Truncated) Discover(string? path, string kind, int maxFiles)
    {
        var root = ResolveRoot(path);
        var files = new List<string>();
        var truncated = false;

        bool Accept(string file) =>
            (kind is "both" or "html" && MarkupAnalyzer.IsHtml(file)) ||
            (kind is "both" or "css" && MarkupAnalyzer.IsCss(file));

        if (File.Exists(root))
        {
            if (Accept(root)) files.Add(root);
            return (Path.GetDirectoryName(root) ?? root, files, false);
        }

        if (!Directory.Exists(root))
            return (root, files, false);

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            string[] children;
            try { children = Directory.GetDirectories(directory); }
            catch { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".artifacts", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(child);
            }

            string[] entries;
            try { entries = Directory.GetFiles(directory); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (!Accept(entry)) continue;
                if (files.Count >= maxFiles) { truncated = true; break; }
                files.Add(entry);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (root, files, truncated);
    }

    private string ResolveRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

        var solutionPath = _workspace.LoadedSolutionPath;
        if (!string.IsNullOrWhiteSpace(solutionPath))
            return Path.GetDirectoryName(solutionPath)!;

        return Directory.GetCurrentDirectory();
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    private static int Rank(string severity) => severity switch
    {
        "error" => 3,
        "warning" => 2,
        _ => 1,
    };
}
