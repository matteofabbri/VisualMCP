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
    private readonly IRoslynWorkspace _workspace;

    public JavaScriptService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> InventoryAsync(string? path, int maxFiles)
    {
        maxFiles = Math.Clamp(maxFiles, 1, 20_000);
        var (root, files, truncated) = Discover(path, maxFiles);
        var scanned = files.Select(file => Path.GetFullPath(file)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<object>();
        var edges = new List<(string From, string To)>();
        var totalImports = 0;
        var totalFindings = 0;
        var totalUnresolvedImports = 0;
        var totalExternalImports = 0;

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;

            totalImports += analysis.Imports.Count;
            totalFindings += analysis.Findings.Count;

            var unresolved = analysis.Findings.Count(finding => finding.RuleId == "JS0017");
            totalUnresolvedImports += unresolved;

            // A specifier is followed wherever the resolver points it: a relative path, a tsconfig alias
            // or a package in node_modules. Only a target the scan contains becomes an edge, so the graph
            // stays closed over the scanned scripts; a dependency outside them is counted, not walked.
            var targets = analysis.ImportReferences
                .Select(reference => JavaScriptPaths.Resolve(file, reference.Module))
                .Where(target => target is not null)
                .ToList();
            var external = targets.Count(target => !scanned.Contains(target!));
            totalExternalImports += external;

            var name = JavaScriptPaths.Relative(root, file);
            var dependsOn = targets
                .Where(target => scanned.Contains(target!))
                .Select(target => JavaScriptPaths.Relative(root, target!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var target in dependsOn)
                edges.Add((name, target));

            entries.Add(new
            {
                file = name,
                lines = analysis.Lines,
                typeScript = analysis.TypeScript,
                jsx = analysis.Jsx,
                imports = analysis.Imports.Count,
                resolvedImports = targets.Count,
                externalImports = external,
                unresolvedImports = unresolved,
                modules = analysis.Imports.Take(50).ToList(),
                dependsOn = dependsOn.Take(50).ToList(),
                exports = analysis.Exports.Take(50).ToList(),
                functions = analysis.Functions,
                classes = analysis.Classes,
                findings = analysis.Findings.Count,
            });
        }

        var moduleEdges = edges
            .Distinct()
            .OrderBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.To, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var (cycles, cyclesSkipped) = FindCycles(moduleEdges);

        return new
        {
            root,
            fileCount = entries.Count,
            truncated,
            maxFiles,
            totalImports,
            totalUnresolvedImports,
            totalExternalImports,
            totalFindings,
            moduleGraph = new
            {
                nodeCount = entries.Count,
                edgeCount = moduleEdges.Count,
                edges = moduleEdges.Take(500).Select(edge => new { from = edge.From, to = edge.To }).ToList(),
                edgesTruncated = moduleEdges.Count > 500,
                cycleCount = cycles.Count,
                cycles = cycles.Take(20).ToList(),
                cyclesTruncated = cycles.Count > 20,
                cyclesSkipped,
            },
            files = entries,
            hint = "analyze_js lists the findings themselves; js_files only says what is there. " +
                   "resolvedImports counts the specifiers that point at a file: a relative path, a " +
                   "tsconfig baseUrl/paths alias, or a package in node_modules; externalImports are the " +
                   "resolved ones outside the scan, and unresolvedImports the relative ones that point at " +
                   "no file (JS0017). moduleGraph draws an edge between two scanned scripts and reports " +
                   "each strongly connected group as one import cycle.",
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
                findings.Add(finding with { File = JavaScriptPaths.Relative(root, file) });
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
                   "module resolves or a type holds. JS0017 resolves a relative import against the file system " +
                   "(the path itself, a source extension, or an index file); an alias or a package specifier is " +
                   "not a defect here — js_files and js_symbols resolve those for the module graph. " +
                   "Template literals are masked whole, so a rule inside `${…}` does not fire; regular-expression " +
                   "literals are not detected and a rule may fire inside one.",
        };
    }

    private async Task<JsAnalysis?> AnalyzeFileAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 2_000_000) return null;
            var text = await File.ReadAllTextAsync(file);
            var analysis = JavaScriptAnalyzer.AnalyzeText(text, file);

            // JS0017 stays relative-only: an alias or a package is followed when it resolves, but a
            // scanner cannot tell a missing package from one the bundler provides, so it is not a defect.
            var unresolved = analysis.ImportReferences
                .Where(reference => reference.Relative)
                .Where(reference => JavaScriptPaths.ResolveRelative(file, reference.Module) is null)
                .Select(reference => new JsFinding(
                    "JS0017", "warning", "Correctness",
                    $"The relative import '{reference.Module}' does not resolve to a file.",
                    file, reference.Line, reference.Column, JavaScriptPaths.SnippetAt(text, reference.Line),
                    "Fix the path; a directory resolves through index.ts/index.js."))
                .ToList();

            return unresolved.Count == 0
                ? analysis
                : analysis with
                {
                    Findings = analysis.Findings.Concat(unresolved)
                        .OrderBy(finding => finding.Line)
                        .ThenBy(finding => finding.Column)
                        .ToList(),
                };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The import cycles of the module graph: one entry per strongly connected component with more than one
    /// module, or one module that imports itself. An edge is a specifier <see cref="JavaScriptPaths.Resolve"/>
    /// points at another scanned script — a relative path, a tsconfig alias or a package inside the scan — so
    /// a dependency outside the scanned files never creates one. Above 2000 modules the walk is skipped and
    /// the caller is told.
    /// </summary>
    private static (List<List<string>> Cycles, bool Skipped) FindCycles(List<(string From, string To)> edges)
    {
        var adjacency = edges
            .GroupBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            nodes.Add(edge.From);
            nodes.Add(edge.To);
        }

        if (nodes.Count > 2_000) return ([], true);

        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var cycles = new List<List<string>>();

        void Visit(string node)
        {
            indices[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            if (adjacency.TryGetValue(node, out var targets))
            {
                foreach (var target in targets)
                {
                    if (!indices.ContainsKey(target))
                    {
                        Visit(target);
                        lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                    }
                    else if (onStack.Contains(target))
                    {
                        lowLinks[node] = Math.Min(lowLinks[node], indices[target]);
                    }
                }
            }

            if (lowLinks[node] != indices[node]) return;

            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (!member.Equals(node, StringComparison.OrdinalIgnoreCase));

            var selfLoop = component.Count == 1 &&
                           adjacency.TryGetValue(component[0], out var self) &&
                           self.Contains(component[0], StringComparer.OrdinalIgnoreCase);
            if (component.Count > 1 || selfLoop)
            {
                component.Sort(StringComparer.OrdinalIgnoreCase);
                cycles.Add(component);
            }
        }

        foreach (var node in nodes)
            if (!indices.ContainsKey(node))
                Visit(node);

        return (cycles.OrderBy(cycle => cycle[0], StringComparer.OrdinalIgnoreCase).ToList(), false);
    }

    private (string Root, List<string> Files, bool Truncated) Discover(string? path, int maxFiles) =>
        JavaScriptPaths.Discover(ResolveRoot(path), maxFiles);

    private string ResolveRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

        var solutionPath = _workspace.LoadedSolutionPath;
        if (!string.IsNullOrWhiteSpace(solutionPath))
            return Path.GetDirectoryName(solutionPath)!;

        return Directory.GetCurrentDirectory();
    }

    private static int Rank(string severity) => severity switch
    {
        "error" => 3,
        "warning" => 2,
        _ => 1,
    };
}
