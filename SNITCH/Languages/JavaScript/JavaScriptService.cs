using Snitch.Workspace;

namespace Snitch.Languages.JavaScript;

/// <summary>Discovery and orchestration over the solution's JavaScript and TypeScript files.</summary>
public interface IJavaScriptService
{
    /// <summary>What the script files are: modules imported and exported, declarations, JSX, finding counts.</summary>
    Task<object> InventoryAsync(string? path, int maxFiles);

    /// <summary>The findings of the rule set, filtered by severity and rule id.</summary>
    Task<object> AnalyzeAsync(string? path, string minSeverity, string? rule, int maxFiles, int maxFindings);
}

public sealed class JavaScriptService : IJavaScriptService
{
    private static readonly string[] ExcludedDirectories =
    [
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "dist", "build", "out",
        "coverage", ".next", ".nuxt", ".svelte-kit", "target", "vendor",
    ];

    private readonly IRoslynWorkspace _workspace;

    public JavaScriptService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InventoryAsync(string? path, int maxFiles)
    {
        maxFiles = Math.Clamp(maxFiles, 1, 20_000);
        var (root, files, truncated) = Discover(path, maxFiles);

        var entries = new List<object>();
        var totalImports = 0;
        var totalFindings = 0;

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;

            totalImports += analysis.Imports.Count;
            totalFindings += analysis.Findings.Count;
            entries.Add(new
            {
                file = Relative(root, file),
                lines = analysis.Lines,
                typeScript = analysis.TypeScript,
                jsx = analysis.Jsx,
                imports = analysis.Imports.Count,
                modules = analysis.Imports.Take(50).ToList(),
                exports = analysis.Exports.Take(50).ToList(),
                functions = analysis.Functions,
                classes = analysis.Classes,
                findings = analysis.Findings.Count,
            });
        }

        return new
        {
            root,
            fileCount = entries.Count,
            truncated,
            maxFiles,
            totalImports,
            totalFindings,
            files = entries,
            hint = "analyze_js lists the findings themselves; js_files only says what is there.",
        };
    }

    public async Task<object> AnalyzeAsync(
        string? path, string minSeverity, string? rule, int maxFiles, int maxFindings)
    {
        var severity = (minSeverity ?? "info").Trim().ToLowerInvariant();
        if (severity is not ("info" or "warning" or "error"))
            return new { error = "minSeverity must be info, warning or error." };

        var ruleId = string.IsNullOrWhiteSpace(rule) ? null : rule.Trim().ToUpperInvariant();
        if (ruleId is not null && !JavaScriptAnalyzer.Rules.ContainsKey(ruleId))
            return new
            {
                error = $"Unknown rule '{rule}'.",
                rules = JavaScriptAnalyzer.Rules.Select(pair => new { id = pair.Key, proves = pair.Value }).ToList(),
            };

        maxFiles = Math.Clamp(maxFiles, 1, 20_000);
        maxFindings = Math.Clamp(maxFindings, 1, 5_000);

        var (root, files, truncated) = Discover(path, maxFiles);
        var findings = new List<JsFinding>();
        var analyzed = 0;

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;
            analyzed++;

            foreach (var finding in analysis.Findings)
            {
                if (Rank(finding.Severity) < Rank(severity)) continue;
                if (ruleId is not null && finding.RuleId != ruleId) continue;
                findings.Add(finding with { File = Relative(root, file) });
            }
        }

        var shown = findings.Take(maxFindings).ToList();
        return new
        {
            root,
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
            note = "A pattern scan over masked source, not a parser: it proves the shape of a line, never that a " +
                   "module resolves or a type holds. Template literals are masked whole, so a rule inside `${…}` " +
                   "does not fire; regular-expression literals are not detected and a rule may fire inside one.",
        };
    }

    private async Task<JsAnalysis?> AnalyzeFileAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 2_000_000) return null;
            var text = await File.ReadAllTextAsync(file);
            return JavaScriptAnalyzer.AnalyzeText(text, file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private (string Root, List<string> Files, bool Truncated) Discover(string? path, int maxFiles)
    {
        var root = ResolveRoot(path);
        var files = new List<string>();
        var truncated = false;

        if (File.Exists(root))
        {
            files.Add(root);
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
                if (!JavaScriptAnalyzer.IsScript(entry)) continue;
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
