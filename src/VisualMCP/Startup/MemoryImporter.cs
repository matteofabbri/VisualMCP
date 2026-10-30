using System.Text;
using System.Text.RegularExpressions;

namespace VisualMCP.Startup;

/// <summary>
/// Carries the project memory that sits in another client's store into the one the current client reads:
/// notes written under <c>~/.claude/projects/&lt;slug&gt;/memory</c> (where earlier VisualMCP builds put them,
/// because memory belongs to the client) are copied into the client's own store, so switching client no
/// longer strands them.
/// </summary>
/// <remarks>
/// The pass is conservative and idempotent: it only creates files that do not exist yet and appends index
/// lines that are missing, so a second start imports nothing and never rewrites a note the client owns.
/// It runs once per process (startup warms it, the docs tool may await it) and never throws: every failure
/// becomes a sentence in the report's notes.
/// </remarks>
public sealed class MemoryImporter : IMemoryImporter
{
    private const string IndexFileName = "MEMORY.md";

    /// <summary>A markdown link in an index line, e.g. <c>- [Title](file.md) — hook</c>.</summary>
    private static readonly Regex IndexLink =
        new(@"\]\((?<file>[^)]+\.md)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly MemoryImportOptions _options;
    private readonly Lazy<Task<MemoryImportReport>> _run;
    private int _noticeTaken;

    /// <summary>Creates the importer for one project; the scan starts on the first <see cref="RunAsync"/>.</summary>
    public MemoryImporter(MemoryImportOptions options)
    {
        _options = options;
        _run = new(() => Task.Run(Import), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc/>
    public Task<MemoryImportReport> RunAsync() => _run.Value;

    /// <inheritdoc/>
    public async Task<MemoryImportReport?> TakeNoticeAsync()
    {
        if (Interlocked.CompareExchange(ref _noticeTaken, 1, 0) != 0) return null;
        var report = await RunAsync().ConfigureAwait(false);
        return report.ImportedAnything ? report : null;
    }

    private MemoryImportReport Import()
    {
        var notes = new List<string>();

        if (!_options.Enabled)
        {
            notes.Add("The memory import is off (VISUALMCP_MEMORY_IMPORT=off).");
            return new MemoryImportReport { Enabled = false, Notes = notes };
        }

        var sources = FindSourceDirectories(notes);
        if (sources.Count == 0) return new MemoryImportReport { Notes = notes };

        var target = FindClientMemoryDirectory(notes);
        if (target is null)
        {
            notes.Add(
                "No memory store for this project was found under '~/.zcode/cli/memories/projects', so nothing " +
                "was imported and the notes stay where they are. Import into your own memory from the path above " +
                "if the client does not pick them up.");
            return new MemoryImportReport { Sources = sources, Notes = notes };
        }

        var imported = new List<string>();
        var present = new List<string>();
        var oversized = new List<string>();
        var indexLines = new List<string>();
        var room = Math.Max(0, _options.MaxFiles);

        foreach (var source in sources)
        {
            string[] files;
            try { files = Directory.GetFiles(source, "*.md"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Could not list '{source}': {ex.Message}");
                continue;
            }

            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);

                if (string.Equals(name, IndexFileName, StringComparison.OrdinalIgnoreCase))
                {
                    CollectIndexLines(file, indexLines, notes);
                    continue;
                }

                var destination = Path.Combine(target, name);
                if (File.Exists(destination))
                {
                    present.Add(destination);
                    continue;
                }

                if (Size(file, notes) > _options.MaxFileBytes)
                {
                    oversized.Add(file);
                    continue;
                }

                if (room == 0)
                {
                    notes.Add($"Stopped at {_options.MaxFiles} note(s): the rest were not imported.");
                    break;
                }

                try
                {
                    Directory.CreateDirectory(target);
                    File.Copy(file, destination);
                    imported.Add(destination);
                    room--;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notes.Add($"Could not copy '{file}': {ex.Message}");
                }
            }
        }

        var addedIndexLines = MergeIndex(target, indexLines, notes);

        return new MemoryImportReport
        {
            Sources = sources,
            Target = target,
            Imported = imported,
            AlreadyPresent = present,
            Oversized = oversized,
            IndexLinesAdded = addedIndexLines,
            Notes = notes,
        };
    }

    /// <summary>The per-project memory stores of the other client(s) that belong to this project.</summary>
    private List<string> FindSourceDirectories(List<string> notes)
    {
        var found = new List<string>();
        var projects = Path.Combine(_options.HomeDirectory, ".claude", "projects");
        if (!Directory.Exists(projects)) return found;

        var slug = MemoryImportOptions.ClaudeSlug(_options.ProjectRoot);

        string[] directories;
        try { directories = Directory.GetDirectories(projects); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not list '{projects}': {ex.Message}");
            return found;
        }

        foreach (var directory in directories)
        {
            if (!string.Equals(Path.GetFileName(directory), slug, StringComparison.OrdinalIgnoreCase)) continue;

            var memory = Path.Combine(directory, "memory");
            if (HasNotes(memory)) found.Add(memory);
        }

        return found;
    }

    /// <summary>
    /// The current client's memory directory for this project: the single store under
    /// <c>~/.zcode/cli/memories/projects</c> named after the project folder (the host appends its own id).
    /// </summary>
    private string? FindClientMemoryDirectory(List<string> notes)
    {
        var root = Path.Combine(
            _options.HomeDirectory, ".zcode", "cli", "memories", "projects");
        if (!Directory.Exists(root)) return null;

        var projectName = Path.GetFileName(
            _options.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(projectName)) return null;

        string[] stores;
        try { stores = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not list '{root}': {ex.Message}");
            return null;
        }

        var matches = stores
            .Where(store => IsStoreOf(Path.GetFileName(store), projectName))
            .ToList();

        if (matches.Count == 1) return Path.Combine(matches[0], "memory");

        if (matches.Count > 1)
        {
            notes.Add(
                $"{matches.Count} memory stores match this project ({string.Join(", ", matches.Select(Path.GetFileName))}); " +
                "nothing was imported so the notes cannot land in the wrong one.");
        }

        return null;
    }

    private static bool IsStoreOf(string storeName, string projectName)
        => string.Equals(storeName, projectName, StringComparison.OrdinalIgnoreCase)
        || storeName.StartsWith(projectName + "-", StringComparison.OrdinalIgnoreCase);

    private static bool HasNotes(string directory)
    {
        try { return Directory.Exists(directory) && Directory.GetFiles(directory, "*.md").Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static long Size(string path, List<string> notes)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not measure '{path}': {ex.Message}");
            return 0;
        }
    }

    /// <summary>The index lines of one source <c>MEMORY.md</c>, kept verbatim.</summary>
    private static void CollectIndexLines(string path, List<string> lines, List<string> notes)
    {
        try { lines.AddRange(File.ReadAllLines(path).Where(line => IndexLink.IsMatch(line))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not read '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Appends the index lines whose note the client's index does not mention yet, keyed on the link target
    /// (not the whole line), so a differently worded entry for the same note is not duplicated.
    /// </summary>
    private static int MergeIndex(string targetDirectory, List<string> lines, List<string> notes)
    {
        if (lines.Count == 0) return 0;

        var indexPath = Path.Combine(targetDirectory, IndexFileName);

        string existing;
        try { existing = File.Exists(indexPath) ? File.ReadAllText(indexPath) : string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not read '{indexPath}': {ex.Message}");
            return 0;
        }

        var known = new HashSet<string>(
            IndexLink.Matches(existing).Select(match => Path.GetFileName(match.Groups["file"].Value)),
            StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var line in lines)
        {
            var match = IndexLink.Match(line);
            if (!match.Success) continue;
            if (!known.Add(Path.GetFileName(match.Groups["file"].Value))) continue;
            added.Add(line.TrimEnd());
        }

        if (added.Count == 0) return 0;

        try
        {
            Directory.CreateDirectory(targetDirectory);

            var text = new StringBuilder();
            if (existing.Length == 0) text.Append("# Memory Index\n\n");
            else
            {
                text.Append(existing);
                if (!existing.EndsWith('\n')) text.Append('\n');
                if (!existing.EndsWith("\n\n", StringComparison.Ordinal)) text.Append('\n');
            }

            foreach (var line in added) text.Append(line).Append('\n');

            File.WriteAllText(indexPath, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not update '{indexPath}': {ex.Message}");
            return 0;
        }

        return added.Count;
    }
}
