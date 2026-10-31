using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Snitch.Planning;

namespace VisualMCP.Startup;

/// <summary>
/// Imports the project memory that sits in the clients' file stores onto the board, once per process.
/// </summary>
/// <remarks>
/// <para>
/// Memory used to be a file per note under <c>~/.claude/projects/&lt;slug&gt;/memory</c> or
/// <c>~/.zcode/cli/memories/projects/&lt;name&gt;-&lt;id&gt;/memory</c>, which no session reads unless it is told
/// to. This pass reads both stores and writes each note through <c>board_remember</c>, so the notes become rows
/// of <c>board_memory</c>: recalled with <c>board_recall</c>, carried by <c>board_brief</c> when pinned, and in
/// the same database as the plan. The files are left where they are — the client owns them; they simply stop
/// being the source of truth.
/// </para>
/// <para>
/// The pass is idempotent: the topic comes from the note's frontmatter and the body is compared with the current
/// version, so a second start imports nothing and a changed note supersedes the previous version instead of
/// duplicating it. It runs once per process (startup warms it, the docs tool may await it) and never throws:
/// every failure becomes a sentence in the report's notes.
/// </para>
/// </remarks>
public sealed class MemoryImporter : IMemoryImporter
{
    private const string IndexFileName = "MEMORY.md";

    /// <summary>A leading <c>---</c> block: the note's name, type and description.</summary>
    private static readonly Regex FrontMatter = new(
        @"\A---\r?\n(?<fields>.*?)\r?\n---\r?\n?", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// One frontmatter field. The indentation is optional on purpose: this client's notes nest
    /// <c>type</c> under <c>metadata:</c> (<c>  type: feedback</c>), while the older Claude notes put it at
    /// the top level, and both spellings describe the same note.
    /// </summary>
    private static readonly Regex FrontMatterField = new(
        @"^\s*(?<key>[A-Za-z_]+):\s*(?<value>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly MemoryImportOptions _options;
    private readonly IBoardService _board;
    private readonly Lazy<Task<MemoryImportReport>> _run;
    private int _noticeTaken;

    /// <summary>Creates the importer for one project; the scan starts on the first <see cref="RunAsync"/>.</summary>
    public MemoryImporter(MemoryImportOptions options, IBoardService board)
    {
        _options = options;
        _board = board;
        _run = new(() => Task.Run(ImportAsync), LazyThreadSafetyMode.ExecutionAndPublication);
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

    private async Task<MemoryImportReport> ImportAsync()
    {
        var notes = new List<string>();

        if (!_options.Enabled)
        {
            notes.Add("The memory import is off (VISUALMCP_MEMORY_IMPORT=off).");
            return new MemoryImportReport { Enabled = false, Notes = notes };
        }

        var sources = FindSourceDirectories(notes);
        if (sources.Count == 0) return new MemoryImportReport { Notes = notes };

        var imported = new List<string>();
        var unchanged = new List<string>();
        var oversized = new List<string>();
        var room = Math.Max(0, _options.MaxFiles);

        foreach (var file in NoteFiles(sources, notes))
        {
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

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"Could not read '{file}': {ex.Message}");
                continue;
            }

            var note = Parse(file, text);
            var result = await _board
                .RememberAsync(note.Topic, note.Body, note.Kind, note.Labels, pinned: false, source: $"{file}#{Hash(text)}")
                .ConfigureAwait(false);

            if (result.Error is not null)
            {
                notes.Add($"Could not import '{file}': {result.Error}");
                continue;
            }

            room--;
            (result.Unchanged ? unchanged : imported).Add(result.Topic);
        }

        return new MemoryImportReport
        {
            Sources = sources,
            Imported = Distinguish(imported),
            AlreadyPresent = Distinguish(unchanged),
            Oversized = oversized,
            Notes = notes,
        };
    }

    /// <summary>The stores that hold notes for this project: Claude Code's and this client's own.</summary>
    private List<string> FindSourceDirectories(List<string> notes)
    {
        var found = new List<string>();

        var projects = Path.Combine(_options.HomeDirectory, ".claude", "projects");
        if (Directory.Exists(projects))
        {
            var slug = MemoryImportOptions.ClaudeSlug(_options.ProjectRoot);
            foreach (var directory in Directories(projects, notes))
            {
                if (!string.Equals(Path.GetFileName(directory), slug, StringComparison.OrdinalIgnoreCase)) continue;

                var memory = Path.Combine(directory, "memory");
                if (HasNotes(memory)) found.Add(memory);
            }
        }

        var stores = Path.Combine(_options.HomeDirectory, ".zcode", "cli", "memories", "projects");
        if (Directory.Exists(stores))
        {
            var projectName = Path.GetFileName(_options.ProjectRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (!string.IsNullOrEmpty(projectName))
            {
                foreach (var store in Directories(stores, notes))
                {
                    if (!IsStoreOf(Path.GetFileName(store), projectName)) continue;

                    var memory = Path.Combine(store, "memory");
                    if (HasNotes(memory) && !found.Contains(memory, StringComparer.OrdinalIgnoreCase))
                        found.Add(memory);
                }
            }
        }

        return found;
    }

    private static bool IsStoreOf(string storeName, string projectName)
        => string.Equals(storeName, projectName, StringComparison.OrdinalIgnoreCase)
        || storeName.StartsWith(projectName + "-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every note file of the stores, in a stable order; the index is not a note.</summary>
    private static IEnumerable<string> NoteFiles(IEnumerable<string> sources, List<string> notes)
    {
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
                if (string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    private static string[] Directories(string root, List<string> notes)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Could not list '{root}': {ex.Message}");
            return [];
        }
    }

    private static bool HasNotes(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.GetFiles(directory, "*.md")
                .Any(file => !string.Equals(Path.GetFileName(file), IndexFileName, StringComparison.OrdinalIgnoreCase));
        }
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

    /// <summary>Frontmatter in, one topic + body out: <c>name</c> and <c>type</c> drive both.</summary>
    private static ImportedNote Parse(string file, string text)
    {
        var type = "project";
        var name = Path.GetFileNameWithoutExtension(file);
        string? description = null;

        var match = FrontMatter.Match(text);
        if (match.Success)
        {
            foreach (Match field in FrontMatterField.Matches(match.Groups["fields"].Value))
            {
                var key = field.Groups["key"].Value;
                var value = field.Groups["value"].Value.Trim().Trim('"').Trim('\'');
                if (value.Length == 0) continue;

                if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                else if (key.Equals("type", StringComparison.OrdinalIgnoreCase)) type = value;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            }
        }

        var body = (match.Success ? text[match.Length..] : text).Trim();
        if (body.Length == 0) body = description ?? name;

        return new ImportedNote(
            $"imported-{Slug(type)}-{Slug(name)}",
            body,
            KindOf(type),
            ["imported", Slug(type)]);
    }

    /// <summary>The board's kinds, from the client's memory types.</summary>
    private static string KindOf(string type) => Slug(type) switch
    {
        "user" => "preference",
        "feedback" => "constraint",
        "reference" => "reference",
        "decision" => "decision",
        "constraint" => "constraint",
        "preference" => "preference",
        _ => "fact",
    };

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }

    /// <summary>The content hash is what makes a changed note a new version and an unchanged one a no-op.</summary>
    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static List<string> Distinguish(List<string> topics) => topics
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToList();

    private sealed record ImportedNote(string Topic, string Body, string Kind, string[] Labels);
}
