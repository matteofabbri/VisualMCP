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
        var edges = new List<(string From, string To)>();
        var totalImports = 0;
        var totalFindings = 0;
        var totalUnresolvedImports = 0;

        foreach (var file in files)
        {
            var analysis = await AnalyzeFileAsync(file);
            if (analysis is null) continue;

            totalImports += analysis.Imports.Count;
            totalFindings += analysis.Findings.Count;

            var unresolved = analysis.Findings.Count(finding => finding.RuleId == "JS0017");
            var relative = analysis.ImportReferences.Count(reference => reference.Relative);
            totalUnresolvedImports += unresolved;

            var name = Relative(root, file);
            var dependsOn = analysis.ImportReferences
                .Where(reference => reference.Relative)
                .Select(reference => ResolveRelative(file, reference.Module))
                .Where(target => target is not null)
                .Select(target => Relative(root, target!))
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
                resolvedImports = relative - unresolved,
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
                   "unresolvedImports counts the relative specifiers that point at no file (JS0017). " +
                   "moduleGraph resolves relative specifiers only — a package specifier needs the project's " +
                   "own resolver — and reports each strongly connected group as one import cycle.",
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
                   "module resolves or a type holds. A relative import IS resolved against the file system " +
                   "(JS0017) — the path itself, a source extension, or an index file; a package specifier is not. " +
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

            // A relative specifier is the one import a scanner can check: it names a file next to this
            // one. A package specifier needs the resolver in node_modules, which the project's own tools own.
            var unresolved = analysis.ImportReferences
                .Where(reference => reference.Relative)
                .Where(reference => ResolveRelative(file, reference.Module) is null)
                .Select(reference => new JsFinding(
                    "JS0017", "warning", "Correctness",
                    $"The relative import '{reference.Module}' does not resolve to a file.",
                    file, reference.Line, reference.Column, SnippetAt(text, reference.Line),
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
    /// module, or one module that imports itself. Only relative specifiers are edges a scanner can prove, so a
    /// package import never creates one. Above 2000 modules the walk is skipped and the caller is told.
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

    /// <summary>The source extensions a relative import may omit, in the order TypeScript resolves them.</summary>
    private static readonly string[] ResolutionSuffixes =
        [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];

    /// <summary>
    /// The file a relative specifier points at: the path itself, the path with a source extension, a
    /// directory index, or the TypeScript source behind a compiled <c>.js</c> specifier. Null when none exists.
    /// </summary>
    private static string? ResolveRelative(string fromFile, string specifier)
    {
        var directory = Path.GetDirectoryName(fromFile);
        if (directory is null) return null;

        var target = Path.GetFullPath(Path.Combine(directory, specifier.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(target)) return target;

        // A TypeScript project imports './x.js' although the source on disk is './x.ts'.
        if (target.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            target.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.ChangeExtension(target, null);
            foreach (var suffix in ResolutionSuffixes)
                if (File.Exists(stem + suffix))
                    return stem + suffix;
        }

        foreach (var suffix in ResolutionSuffixes)
            if (File.Exists(target + suffix))
                return target + suffix;

        foreach (var suffix in ResolutionSuffixes)
        {
            var index = Path.Combine(target, "index" + suffix);
            if (File.Exists(index)) return index;
        }

        return null;
    }

    private static string SnippetAt(string text, int line)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (line < 1 || line > lines.Length) return string.Empty;
        var snippet = lines[line - 1].Trim();
        return snippet.Length <= 160 ? snippet : snippet[..160] + "…";
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
