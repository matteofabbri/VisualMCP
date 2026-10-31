using System.Xml.Linq;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>The portable content of a ReSharper <c>.DotSettings</c> file.</summary>
public sealed record DotSettingsContent(
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, string> InspectionSeverities,
    IReadOnlyList<string> DisabledInspections,
    IReadOnlyList<string> DictionaryWords,
    IReadOnlyList<string> Errors);

/// <summary>
/// Reads the ReSharper <c>.DotSettings</c> layers a team shares (solution and <c>.user</c> files):
/// inspection severities, disabled inspections and the custom spelling dictionary. The severities are
/// ReSharper's own inspection ids, so they are reported rather than applied to Roslyn diagnostics — the
/// portable part is the dictionary, which the spelling inspection uses.
/// </summary>
public interface IDotSettingsService
{
    Task<object> ReadAsync(string? path);
}

public sealed class DotSettingsService : IDotSettingsService
{
    private readonly IRoslynWorkspace _workspace;
    public DotSettingsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ReadAsync(string? path)
    {
        var start = ResolveStart(path);
        if (start is null)
            return new { error = "No solution is loaded and no path was given: pass the .DotSettings file or folder." };

        var files = Collect(start);
        if (files.Count == 0)
            return new
            {
                path = start,
                filesRead = 0,
                note = "No .DotSettings file found. ReSharper writes them next to the solution (MySolution.sln.DotSettings).",
            };

        var content = Parse(files);
        return new
        {
            path = start,
            filesRead = content.Files.Count,
            files = content.Files,
            inspections = content.InspectionSeverities,
            disabled = content.DisabledInspections,
            dictionaryWordCount = content.DictionaryWords.Count,
            dictionaryWords = content.DictionaryWords,
            errors = content.Errors,
            note = "Severities are ReSharper inspection ids (InconsistentNaming, …), reported but not applied to Roslyn " +
                   "diagnostics; configure_inspection writes dotnet_diagnostic.<id>.severity where the compiler reads it. " +
                   "The custom dictionary words feed find_spelling_issues.",
        };
    }

    private string? ResolveStart(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
        if (_workspace.LoadedSolutionPath is { } solutionPath)
            return Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        return Directory.Exists(Directory.GetCurrentDirectory()) ? Directory.GetCurrentDirectory() : null;
    }

    private static List<string> Collect(string start)
    {
        if (File.Exists(start)) return [start];

        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(start, "*.DotSettings*", SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(part => part is "bin" or "obj" or ".git")) continue;
                files.Add(file);
            }
        }
        catch (Exception)
        {
            return files;
        }

        return files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Parses one or more files; public so the format is pinned by a test.</summary>
    public static DotSettingsContent Parse(IReadOnlyList<string> files)
    {
        var severities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new SortedSet<string>(StringComparer.Ordinal);
        var words = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var file in files)
        {
            XDocument document;
            try { document = XDocument.Load(file); }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(file)}: {exception.Message}");
                continue;
            }

            foreach (var element in document.Descendants())
            {
                var key = element.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")?.Value;
                if (key is null) continue;
                var value = element.Value.Trim();

                var severity = Between(key, "/CodeInspection/Highlighting/InspectionSeverities/=");
                if (severity is not null)
                {
                    severities[severity] = value;
                    if (value.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("DO_NOT_SHOW", StringComparison.OrdinalIgnoreCase))
                        disabled.Add(severity);
                    continue;
                }

                var word = Between(key, "/Environment/Spelling/CustomDictionaryWords/=");
                if (word is not null && word.Length > 0) words.Add(word);
            }
        }

        return new DotSettingsContent(files, severities, disabled.ToList(), words.ToList(), errors);
    }

    /// <summary>The id right after a marker segment in a settings key (<c>…/=Id/@Entry…</c>).</summary>
    private static string? Between(string key, string marker)
    {
        var start = key.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        start += marker.Length;
        if (start >= key.Length) return null;

        var end = key.IndexOf('/', start);
        var id = end < 0 ? key[start..] : key[start..end];
        return id.Length == 0 ? null : id;
    }
}
