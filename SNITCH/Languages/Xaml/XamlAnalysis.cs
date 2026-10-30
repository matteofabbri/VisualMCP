using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>
/// Shared plumbing for the XAML tools: resolve the roots from the loaded solution (or from an
/// explicit path), enumerate <c>.xaml</c>/<c>.axaml</c> files, scan them, and resolve the
/// code-behind type through Roslyn. XAML is markup, so — exactly like the Razor layer — the file
/// is read from disk and the C# is resolved from the loaded solution.
/// </summary>
public static class XamlAnalysis
{
    private const long MaxFileBytes = 4_000_000;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".visualMCP", "node_modules", "TestResults",
    };

    /// <summary>Attribute suffixes that mark an event handler, so a plain property is not mistaken for one.</summary>
    private static readonly string[] EventSuffixes =
    [
        "Click", "Changed", "Loaded", "Unloaded", "Tapped", "Pressed", "Released", "Selected",
        "Closing", "Closed", "Opening", "Opened", "Started", "Completed", "Invoked", "Holding",
        "DragOver", "DragEnter", "DragLeave", "Drop", "KeyDown", "KeyUp", "GotFocus", "LostFocus",
        "Checked", "Unchecked", "Expanded", "Collapsed", "Scrolled", "SizeChanged", "PointerMoved",
        "Appearing", "Disappearing", "Navigated", "Navigating", "Hover", "Resize",
    ];

    // ── discovery ────────────────────────────────────────────────────────────
    public static async Task<XamlCollection> CollectAsync(IRoslynWorkspace workspace, string? projectName,
        string? path, int maxFiles)
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
                return new XamlCollection
                {
                    Error = $"Project '{projectName}' not found in the loaded solution. " +
                            "Call check_solution or load_solution, and use the project's exact name.",
                };
        }

        var collection = new XamlCollection { Root = solutionDirectory ?? Directory.GetCurrentDirectory() };

        if (!string.IsNullOrWhiteSpace(path))
        {
            var fullPath = ResolvePath(path, solutionDirectory);
            if (File.Exists(fullPath))
            {
                if (!IsXamlFile(fullPath))
                {
                    collection.Error = $"'{fullPath}' is not a .xaml or .axaml file.";
                    return collection;
                }

                collection.Root = Path.GetDirectoryName(fullPath) ?? collection.Root;
                collection.Files.Add(new XamlFileEntry(fullPath, RelativePath(fullPath, solutionDirectory),
                    ProjectFor(fullPath, projects)));
                return collection;
            }

            if (!Directory.Exists(fullPath))
            {
                collection.Error = $"Path not found: {fullPath}";
                return collection;
            }

            collection.Root = fullPath;
            AddDirectory(collection, fullPath, ProjectFor(fullPath, projects), maxFiles);
            return collection;
        }

        if (solution is null)
        {
            collection.Error =
                "No C# solution could be auto-located from the working directory. " +
                "Call load_solution with an explicit path, or pass path=<folder or .xaml file>.";
            return collection;
        }

        var roots = projects.Count > 0
            ? projects.Select(project => (project.Directory, (string?)project.Name)).ToList()
            : solutionDirectory is null ? [] : [(solutionDirectory, (string?)null)];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, project) in roots)
        {
            foreach (var file in EnumerateXamlFiles(root))
            {
                if (!seen.Add(file)) continue;
                if (collection.Files.Count >= maxFiles)
                {
                    collection.Truncated = true;
                    break;
                }

                collection.Files.Add(new XamlFileEntry(file, RelativePath(file, solutionDirectory),
                    project ?? ProjectFor(file, projects)));
            }

            if (collection.Truncated) break;
        }

        collection.Files.Sort((left, right) =>
            string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return collection;
    }

    /// <summary>Reads and scans one file; an invalid document produces an XAML0008 finding, not an exception.</summary>
    public static XamlDocument? TryRead(XamlFileEntry file, XamlCollection collection, List<XamlFinding> findings)
    {
        try
        {
            var info = new FileInfo(file.FilePath);
            if (info.Length > MaxFileBytes)
            {
                collection.Skipped.Add($"{file.RelativePath} (larger than 4 MB)");
                return null;
            }

            var text = File.ReadAllText(file.FilePath);
            return Scan(file, text);
        }
        catch (XmlException ex)
        {
            findings.Add(Finding("XAML0008", file.FilePath, file.RelativePath, ex.LineNumber,
                $"The file is not well-formed XML: {ex.Message}",
                "Fix the markup before anything else: no XAML tool can read a document that does not parse.", null));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            collection.Unreadable.Add($"{file.RelativePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The scan itself — pure text in, facts out, so a test needs no file system.</summary>
    public static XamlDocument Scan(XamlFileEntry file, string text)
    {
        var xml = XDocument.Parse(text, LoadOptions.SetLineInfo);
        var root = xml.Root;

        var document = new XamlDocument(file.FilePath, file.RelativePath, file.Project, xml)
        {
            ClassName = root is null ? null : AttributeValue(root, "Class"),
            RootName = root?.Name.LocalName ?? string.Empty,
            ElementCount = xml.Descendants().Count(),
        };

        foreach (var element in xml.Descendants())
        {
            var line = LineOf(element);

            foreach (var attribute in element.Attributes())
            {
                var isXamlNamespace = attribute.Name.NamespaceName.Contains("xaml", StringComparison.OrdinalIgnoreCase);
                var value = attribute.Value;

                if (isXamlNamespace && attribute.Name.LocalName == "Name" && !string.IsNullOrWhiteSpace(value))
                    document.Names.Add(new XamlName(value.Trim(), line));

                if (isXamlNamespace && attribute.Name.LocalName == "Key" && !string.IsNullOrWhiteSpace(value))
                    document.Keys.Add(new XamlKey(value.Trim(), line, element.Name.LocalName));

                if (!isXamlNamespace && IsEventHandler(attribute.Name.LocalName, value) && !value.StartsWith('{'))
                    document.Handlers.Add(new XamlHandler(attribute.Name.LocalName, value.Trim(), line));

                CollectReferences(document, value, line);
            }

            if (element.Name.LocalName is "DataTemplate" or "HierarchicalDataTemplate" &&
                AttributeValue(element, "DataType") is null &&
                AttributeInXamlNamespace(element, "DataType") is null)
                document.TemplatesWithoutDataType.Add(line);

            if (element.Name.LocalName == "Style" && AttributeInXamlNamespace(element, "Key") is { } styleKey)
            {
                var basedOn = AttributeValue(element, "BasedOn");
                document.Styles.Add(new XamlStyle(styleKey, ReferenceKey(basedOn), line));
            }
        }

        return document;
    }

    /// <summary>Resolves the code-behind type through Roslyn, with the member names the rules check.</summary>
    public static async Task<XamlTypeFacts?> ResolveTypeAsync(Solution? solution, string? projectName, string className)
    {
        if (solution is null) return null;

        var projects = projectName is null
            ? solution.Projects.ToList()
            : solution.Projects.Where(project =>
                project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            var symbol = compilation?.GetTypeByMetadataName(className);
            if (symbol is null) continue;

            var members = new HashSet<string>(StringComparer.Ordinal);
            for (var current = symbol; current is not null; current = current.BaseType)
                foreach (var member in current.GetMembers())
                    members.Add(member.Name);

            var declared = new HashSet<string>(symbol.GetMembers().Select(member => member.Name), StringComparer.Ordinal);

            var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
            var span = location?.GetLineSpan();

            return new XamlTypeFacts(
                symbol.ToDisplayString(),
                members,
                declared,
                span is null ? null : location!.SourceTree?.FilePath,
                span is null ? null : span.Value.StartLinePosition.Line + 1);
        }

        return null;
    }

    // ── scanning helpers ─────────────────────────────────────────────────────
    private static void CollectReferences(XamlDocument document, string value, int line)
    {
        foreach (var key in KeysIn(value, "StaticResource")) document.ResourceReferences.Add(new XamlResourceReference(key, line, "StaticResource"));
        foreach (var key in KeysIn(value, "DynamicResource")) document.ResourceReferences.Add(new XamlResourceReference(key, line, "DynamicResource"));

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     value, @"(?:ElementName|=)\s*([A-Za-z_][A-Za-z0-9_]*)"))
        {
            var name = match.Groups[1].Value;
            if (name is "True" or "False") continue;
            document.NameReferences.Add(new XamlNameReference(name, line, "ElementName"));
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     value, @"\{x:Reference\s+([A-Za-z_][A-Za-z0-9_]*)"))
            document.NameReferences.Add(new XamlNameReference(match.Groups[1].Value, line, "x:Reference"));
    }

    private static IEnumerable<string> KeysIn(string value, string kind)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     value, @"\{" + kind + @"\s+(?:ResourceKey=)?([A-Za-z0-9_.]+)\s*\}"))
            yield return match.Groups[1].Value;
    }

    private static string? ReferenceKey(string? value)
    {
        if (value is null) return null;
        var keys = KeysIn(value, "StaticResource").Concat(KeysIn(value, "DynamicResource")).ToList();
        return keys.Count > 0 ? keys[0] : null;
    }

    private static bool IsEventHandler(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_.]*$")) return false;
        return EventSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes()
            .Where(attribute => attribute.Name.LocalName == localName)
            .Select(attribute => attribute.Value)
            .FirstOrDefault();

    private static string? AttributeInXamlNamespace(XElement element, string localName) =>
        element.Attributes()
            .Where(attribute => attribute.Name.LocalName == localName &&
                                attribute.Name.NamespaceName.Contains("xaml", StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value)
            .FirstOrDefault();

    public static int LineOf(XObject node) => ((IXmlLineInfo)node).LineNumber;

    // ── findings ─────────────────────────────────────────────────────────────
    public static XamlFinding Finding(string ruleId, string filePath, string relativePath, int line,
        string message, string suggestion, string? snippet)
    {
        var (title, category, severity) = XamlRuleCatalog.Describe(ruleId);
        return new XamlFinding(ruleId, category, title, severity, message, suggestion, filePath, relativePath, line, snippet);
    }

    public static object ToJson(XamlFinding finding) => new
    {
        ruleId = finding.RuleId,
        category = finding.Category,
        title = finding.Title,
        severity = finding.Severity,
        message = finding.Message,
        suggestion = finding.Suggestion,
        file = finding.FilePath,
        relativePath = finding.RelativePath,
        line = finding.Line,
        snippet = finding.Snippet,
    };

    public static List<XamlFinding> Sort(IEnumerable<XamlFinding> findings) => findings
        .OrderByDescending(finding => XamlRuleCatalog.Rank(finding.Severity))
        .ThenBy(finding => finding.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(finding => finding.Line)
        .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
        .ToList();

    public static Dictionary<string, int> CountBy(IEnumerable<XamlFinding> findings, Func<XamlFinding, string> key) =>
        findings.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public static int Clamp(int value, int fallback, int max) => value <= 0 ? fallback : Math.Min(value, max);

    // ── paths ────────────────────────────────────────────────────────────────
    private static void AddDirectory(XamlCollection collection, string root, string? project, int maxFiles)
    {
        foreach (var file in EnumerateXamlFiles(root))
        {
            if (collection.Files.Count >= maxFiles)
            {
                collection.Truncated = true;
                return;
            }

            collection.Files.Add(new XamlFileEntry(file, RelativePath(file, root), project));
        }
    }

    private static IEnumerable<string> EnumerateXamlFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!ExcludedDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                    continue;
                }

                if (IsXamlFile(entry)) yield return entry;
            }
        }
    }

    private static bool IsXamlFile(string path) =>
        path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePath(string path, string? solutionDirectory)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(solutionDirectory ?? Directory.GetCurrentDirectory(), path));
    }

    private static string RelativePath(string file, string? baseDirectory)
    {
        var basePath = baseDirectory ?? Path.GetDirectoryName(file) ?? Directory.GetCurrentDirectory();
        return Path.GetRelativePath(basePath, file).Replace('\\', '/');
    }

    private static string? ProjectFor(string path, List<(string Name, string Directory)> projects)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        string? best = null;
        var bestLength = -1;
        foreach (var (name, directory) in projects)
        {
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, comparison)) continue;
            if (prefix.Length <= bestLength) continue;
            best = name;
            bestLength = prefix.Length;
        }
        return best;
    }
}

/// <summary>The files one call covers, plus what could not be read.</summary>
public sealed class XamlCollection
{
    public string? Error { get; set; }
    public string Root { get; set; } = string.Empty;
    public List<XamlFileEntry> Files { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Unreadable { get; } = [];
    public bool Truncated { get; set; }
}

/// <summary>One XAML file on disk, with the project that owns it.</summary>
public sealed record XamlFileEntry(string FilePath, string RelativePath, string? Project);
