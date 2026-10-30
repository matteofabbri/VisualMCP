using Snitch.Workspace;

namespace Snitch.Languages.Razor;

/// <summary>A Razor file found on disk, with the project it belongs to.</summary>
public sealed record RazorFileEntry(string FilePath, string RelativePath, string? Project);

/// <summary>One discovery pass: the files to analyze plus everything that was skipped.</summary>
public sealed class RazorCollection
{
    public string Root { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool Truncated { get; set; }
    public List<RazorFileEntry> Files { get; } = [];
    public List<string> Unreadable { get; } = [];
    public List<string> Skipped { get; } = [];
}

/// <summary>
/// Shared plumbing for the three Razor tools: resolve the roots from the loaded solution (or from an
/// explicit path), enumerate <c>.cshtml</c>/<c>.razor</c> files, scan them and shape findings.
/// </summary>
public static class RazorAnalysis
{
    private const long MaxFileBytes = 2_000_000;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "TestResults",
    };

    // ── Discovery ──────────────────────────────────────────────────────────────

    public static async Task<RazorCollection> CollectAsync(
        IRoslynWorkspace workspace,
        string? projectName,
        string? path,
        int maxPages)
    {
        var solution = await workspace.EnsureSolutionLoadedAsync();
        var solutionPath = solution?.FilePath ?? workspace.LoadedSolutionPath;
        var solutionDirectory = solutionPath is null ? null : Path.GetDirectoryName(solutionPath);

        var projects = solution?.Projects
            .Where(project => project.FilePath is not null)
            .Select(project => (Name: project.Name, Directory: Path.GetDirectoryName(project.FilePath!)!))
            .Where(project => !string.IsNullOrEmpty(project.Directory) && Directory.Exists(project.Directory))
            .ToList() ?? [];

        if (projectName is not null)
        {
            projects = projects
                .Where(project => project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (projects.Count == 0)
            {
                return new RazorCollection
                {
                    Error = $"Project '{projectName}' not found in the loaded solution. " +
                            "Call check_solution or load_solution, and use the project's exact name.",
                };
            }
        }

        var collection = new RazorCollection
        {
            Root = solutionDirectory ?? Directory.GetCurrentDirectory(),
        };

        if (!string.IsNullOrWhiteSpace(path))
        {
            var fullPath = ResolvePath(path, solutionDirectory);
            if (File.Exists(fullPath))
            {
                if (!IsRazorFile(fullPath))
                {
                    collection.Error = $"'{fullPath}' is not a .cshtml or .razor file.";
                    return collection;
                }

                collection.Root = Path.GetDirectoryName(fullPath) ?? collection.Root;
                collection.Files.Add(new RazorFileEntry(
                    fullPath, RelativePath(fullPath, solutionDirectory), ProjectFor(fullPath, projects)));
                return collection;
            }

            if (!Directory.Exists(fullPath))
            {
                collection.Error = $"Path not found: {fullPath}";
                return collection;
            }

            collection.Root = fullPath;
            AddDirectory(collection, fullPath, ProjectFor(fullPath, projects), maxPages);
            return collection;
        }

        if (solution is null)
        {
            collection.Error =
                "No C# solution could be auto-located from the working directory. " +
                "Call load_solution with an explicit path, or pass path=<folder or .cshtml file>.";
            return collection;
        }

        List<(string DocumentRoot, string? Project)> roots;
        if (projects.Count > 0)
            roots = projects.Select(project => (project.Directory, (string?)project.Name)).ToList();
        else if (solutionDirectory is not null)
            roots = [(solutionDirectory, null)];
        else
            roots = [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, project) in roots)
        {
            foreach (var file in EnumerateRazorFiles(root))
            {
                if (!seen.Add(file)) continue;
                if (collection.Files.Count >= maxPages)
                {
                    collection.Truncated = true;
                    break;
                }

                collection.Files.Add(new RazorFileEntry(
                    file,
                    RelativePath(file, solutionDirectory),
                    project ?? ProjectFor(file, projects)));
            }

            if (collection.Truncated) break;
        }

        collection.Files.Sort((left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return collection;
    }

    /// <summary>Reads and parses one file, recording why it was skipped when it cannot be read.</summary>
    public static RazorDocument? TryRead(RazorFileEntry file, RazorCollection collection)
    {
        try
        {
            var length = new FileInfo(file.FilePath).Length;
            if (length > MaxFileBytes)
            {
                collection.Skipped.Add($"{file.RelativePath} (larger than 2 MB)");
                return null;
            }

            var text = File.ReadAllText(file.FilePath);
            var document = RazorScanner.Scan(file.FilePath, text);
            document.Kind = RazorPageClassifier.Classify(document, file.RelativePath);
            return document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            collection.Unreadable.Add($"{file.RelativePath}: {ex.Message}");
            return null;
        }
    }

    private static void AddDirectory(RazorCollection collection, string root, string? project, int maxPages)
    {
        foreach (var file in EnumerateRazorFiles(root))
        {
            if (collection.Files.Count >= maxPages)
            {
                collection.Truncated = true;
                return;
            }

            collection.Files.Add(new RazorFileEntry(file, RelativePath(file, root), project));
        }
    }

    private static IEnumerable<string> EnumerateRazorFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!ExcludedDirectories.Contains(Path.GetFileName(entry)))
                        pending.Push(entry);
                    continue;
                }

                if (IsRazorFile(entry))
                    yield return entry;
            }
        }
    }

    private static bool IsRazorFile(string path) =>
        path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePath(string path, string? solutionDirectory)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        var baseDirectory = solutionDirectory ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }

    private static string RelativePath(string file, string? baseDirectory)
    {
        var basePath = baseDirectory ?? Path.GetDirectoryName(file) ?? Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(basePath, file);
        return relative.Replace('\\', '/');
    }

    private static string? ProjectFor(string path, List<(string Name, string Directory)> projects)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string? best = null;
        var bestLength = -1;
        foreach (var (name, directory) in projects)
        {
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
                ? directory
                : directory + Path.DirectorySeparatorChar;

            if (!path.StartsWith(prefix, comparison)) continue;
            if (prefix.Length <= bestLength) continue;

            best = name;
            bestLength = prefix.Length;
        }
        return best;
    }

    // ── Findings ───────────────────────────────────────────────────────────────

    public static List<RazorFinding> Sort(IEnumerable<RazorFinding> findings) =>
        findings
            .OrderByDescending(finding => RazorRuleCatalog.Rank(finding.Severity))
            .ThenBy(finding => finding.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.Line)
            .ThenBy(finding => finding.Column)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ToList();

    public static object ToJson(RazorFinding finding) => new
    {
        ruleId = finding.RuleId,
        category = finding.Category,
        title = finding.Title,
        severity = finding.Severity,
        cwe = finding.Cwe,
        message = finding.Message,
        suggestion = finding.Suggestion,
        file = finding.FilePath,
        line = finding.Line,
        column = finding.Column,
        snippet = finding.Snippet,
    };

    public static Dictionary<string, int> CountBy(IEnumerable<RazorFinding> findings, Func<RazorFinding, string> key) =>
        findings
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public static int Clamp(int value, int fallback, int max) =>
        value <= 0 ? fallback : Math.Min(value, max);

    public static bool IsKnownSeverity(string? severity) =>
        severity is null || RazorRuleCatalog.Severities.Contains(severity, StringComparer.OrdinalIgnoreCase);

    public static string KindName(RazorPageKind kind) => kind switch
    {
        RazorPageKind.Page => "page",
        RazorPageKind.View => "view",
        RazorPageKind.Partial => "partial",
        RazorPageKind.Layout => "layout",
        RazorPageKind.Component => "component",
        RazorPageKind.Imports => "imports",
        RazorPageKind.ViewStart => "viewstart",
        _ => "other",
    };
}

/// <summary>Path- and directive-based classification, because the extension alone cannot tell them apart.</summary>
public static class RazorPageClassifier
{
    public static RazorPageKind Classify(RazorDocument document, string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        // Normalised so "Views/Home/Index.cshtml" (a path relative to the project root) and
        // "/Views/Home/Index.cshtml" both match the same segment test.
        var normalisedPath = "/" + relativePath.Replace('\\', '/');

        if (fileName.Equals("_ViewImports.cshtml", StringComparison.OrdinalIgnoreCase))
            return RazorPageKind.Imports;
        if (fileName.Equals("_ViewStart.cshtml", StringComparison.OrdinalIgnoreCase))
            return RazorPageKind.ViewStart;

        if (relativePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            return fileName.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase)
                ? RazorPageKind.Imports
                : RazorPageKind.Component;

        if (document.HasDirective("page"))
            return RazorPageKind.Page;

        var isLayout = fileName.Contains("layout", StringComparison.OrdinalIgnoreCase) ||
                       document.Expressions.Any(expression =>
                           expression.Text.StartsWith("RenderBody", StringComparison.Ordinal) ||
                           expression.Text.StartsWith("RenderSection", StringComparison.Ordinal));
        if (isLayout)
            return RazorPageKind.Layout;

        if (fileName.StartsWith('_'))
            return RazorPageKind.Partial;

        if (normalisedPath.Contains("/Views/", StringComparison.OrdinalIgnoreCase))
            return RazorPageKind.View;

        return document.ModelType is not null || document.LayoutDirective is not null
            ? RazorPageKind.View
            : RazorPageKind.Other;
    }
}
