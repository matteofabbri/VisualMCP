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
        var stylesheets = analyses.Where(entry => entry.Analysis.Kind == "css").ToList();
        var nodes = pages.SelectMany(entry => entry.Analysis.Nodes ?? []).ToList();

        if (pages.Count > 0 && stylesheets.Count > 0)
        {
            notes.Add("CSS0009 resolves only simple selectors (tag, .class, #id, tag.class) against the tag/class/id " +
                      "index of the scanned pages; a combinator, an attribute, a pseudo-class and the cascade are " +
                      "not resolved.");

            foreach (var (file, analysis) in stylesheets)
            {
                foreach (var selector in analysis.Selectors ?? [])
                {
                    if (nodes.Any(node => MarkupAnalyzer.Matches(node, selector))) continue;
                    AddIfSelected(UnmatchedSelector(file, selector));
                }
            }

            var defined = stylesheets
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
            notes.Add("Selector matching was skipped: no stylesheet is in scope, so CSS0009 and HTML0015 cannot be judged.");
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
            $"The selector '{selector.Text}' matches no tag, class or id in the scanned pages.",
            file, selector.Line, selector.Column, selector.Text,
            "Remove the rule, or check the spelling: a typo here is a style that never applies.");

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
