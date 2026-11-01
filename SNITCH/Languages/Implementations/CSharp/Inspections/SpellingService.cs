using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// A spelling inspection without a bundled dictionary: it flags words that are one edit away from a word
/// the solution (or the repository dictionary) already uses — <c>Calcualte</c> next to <c>Calculate</c>,
/// <c>recieve</c> next to <c>receive</c>. Zero configuration works for typos that repeat; a
/// <c>.visualMCP/spelling.dic</c> file and the <c>.DotSettings</c> custom words extend the known words.
/// </summary>
public interface ISpellingService
{
    Task<object> FindAsync(string? projectName, string? filePath, string? dictionaryPath, int maxResults);
}

public sealed class SpellingService : ISpellingService
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while", "var", "async", "await", "dynamic", "nameof",
        "record", "init", "required", "file", "scoped", "when", "with", "yield",
    };

    /// <summary>Identifier words too common to be a typo of anything.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "set", "add", "remove", "value", "args", "arg", "item", "items", "data", "list", "name",
        "type", "key", "text", "path", "file", "line", "count", "index", "node", "tree", "root", "id",
        "url", "uri", "http", "json", "xml", "html", "sql", "api", "db", "io", "ok", "error", "errors",
        "result", "results", "task", "tasks", "test", "tests", "mock", "mocks", "fake", "stub", "util",
        "utils", "helper", "helpers", "manager", "service", "services", "model", "models", "view", "views",
        "code", "run", "log", "logs", "main", "init", "ctor", "dto", "guid", "uuid", "enum", "flag", "flags",
        "self", "base", "inner", "outer", "left", "right", "next", "prev", "first", "last", "min", "max",
        "src", "dst", "tmp", "temp", "core", "config", "options", "settings", "props", "ref", "refs",
    };

    private readonly IRoslynWorkspace _workspace;
    public SpellingService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> FindAsync(string? projectName, string? filePath, string? dictionaryPath, int maxResults)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var solutionDirectory = _workspace.LoadedSolutionPath is { } solutionPath
            ? Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Directory.GetCurrentDirectory()
            : solution.Projects.Select(project => project.FilePath)
                .Where(path => path is not null)
                .Select(path => Path.GetDirectoryName(Path.GetFullPath(path!)))
                .FirstOrDefault(directory => directory is not null) ?? Directory.GetCurrentDirectory();

        var dictionary = await LoadAsync(dictionaryPath, solutionDirectory);
        var occurrences = new Dictionary<string, WordInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var document in project.Documents.Where(document => document.SourceCodeKind == SourceCodeKind.Regular))
            {
                if (filePath is not null && !string.Equals(Path.GetFullPath(document.FilePath ?? string.Empty),
                        Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var root = await document.GetSyntaxRootAsync();
                if (root is null) continue;

                foreach (var (word, line, kind) in Tokens(root))
                {
                    if (!Consider(word, dictionary)) continue;

                    var key = word.ToLowerInvariant();
                    if (!occurrences.TryGetValue(key, out var info))
                        info = new WordInfo(word, kind);
                    occurrences[key] = info with
                    {
                        Count = info.Count + 1,
                        Locations = info.Locations.Count >= 5 ? info.Locations : [.. info.Locations, $"{document.FilePath}:{line}"],
                    };
                }
            }
        }

        var candidates = new List<Candidate>();
        foreach (var (word, info) in occurrences)
        {
            var suggestion = Suggest(word, info.Count, occurrences, dictionary, out var fromDictionary);
            if (suggestion is null) continue;

            candidates.Add(new Candidate(info.Display, suggestion, fromDictionary ? "dictionary" : "solution",
                info.Count, info.Kind, info.Locations));
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Occurrences)
            .ThenBy(candidate => candidate.Word, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var limit = Math.Clamp(maxResults < 1 ? 100 : maxResults, 1, 1000);
        return new
        {
            projectFilter = projectName ?? "all",
            dictionary = dictionary.Path,
            dictionaryWords = dictionary.Words.Count,
            scannedWords = occurrences.Count,
            typoCandidates = ordered.Count,
            findings = ordered.Take(limit).Select(candidate => new
            {
                word = candidate.Word,
                suggestion = candidate.Suggestion,
                suggestionSource = candidate.Source,
                candidate.Occurrences,
                seenIn = candidate.Kind,
                candidate.Locations,
            }).ToList(),
            truncated = ordered.Count > limit,
            note = "A candidate is a word one edit away from a word the solution or the dictionary already uses; " +
                   "add a word to the dictionary file to silence it. Comments and identifiers are scanned; " +
                   "string literals are not, because their vocabulary is data.",
        };
    }

    private sealed record WordInfo(string Display, string Kind)
    {
        public int Count { get; init; } = 1;
        public IReadOnlyList<string> Locations { get; init; } = [];
    }

    private sealed record Candidate(
        string Word, string Suggestion, string Source, int Occurrences, string Kind, IReadOnlyList<string> Locations);

    /// <summary>Words worth considering: letters only, long enough, not a keyword or a common code word.</summary>
    private static bool Consider(string word, KnownWords dictionary) =>
        word.Length >= 3 && word.Length <= 30 &&
        word.All(char.IsLetter) &&
        !LooksLikeAnAcronym(word) &&
        !Keywords.Contains(word) &&
        !Common.Contains(word) &&
        !dictionary.Words.Contains(word);

    /// <summary><c>XMLHttp</c> and <c>ID</c> are not typos: three uppercase letters in a row are an acronym.</summary>
    private static bool LooksLikeAnAcronym(string word)
    {
        var run = 0;
        foreach (var character in word)
        {
            run = char.IsUpper(character) ? run + 1 : 0;
            if (run >= 3) return true;
        }

        return word.Length <= 4 && word.All(char.IsUpper);
    }

    /// <summary>
    /// The closest known word one edit away, preferring the dictionary over the solution and a more
    /// frequent word over a rarer one; a solution word needs at least two uses to be evidence.
    /// </summary>
    private static string? Suggest(
        string word,
        int count,
        IReadOnlyDictionary<string, WordInfo> occurrences,
        KnownWords dictionary,
        out bool fromDictionary)
    {
        fromDictionary = false;

        foreach (var candidate in dictionary.Words)
            if (candidate.Length >= 3 && IsOneEditApart(word, candidate))
            {
                fromDictionary = true;
                return candidate;
            }

        string? best = null;
        var bestCount = 0;
        foreach (var (candidate, info) in occurrences)
        {
            if (candidate.Equals(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (info.Count < 2 || info.Count <= count) continue;
            if (!IsOneEditApart(word, candidate)) continue;
            if (info.Count <= bestCount) continue;
            best = info.Display;
            bestCount = info.Count;
        }

        return best;
    }

    /// <summary>One insertion, deletion, substitution or adjacent transposition.</summary>
    public static bool IsOneEditApart(string left, string right)
    {
        var a = left.ToLowerInvariant();
        var b = right.ToLowerInvariant();
        if (a == b) return false;
        if (Math.Abs(a.Length - b.Length) > 1) return false;

        if (a.Length == b.Length)
        {
            var first = -1;
            for (var index = 0; index < a.Length; index++)
            {
                if (a[index] == b[index]) continue;

                if (first < 0)
                {
                    first = index;
                    continue;
                }

                // A second difference is one edit only when it is the swap of the first pair.
                return index == first + 1 && a[first] == b[index] && a[index] == b[first];
            }

            return first >= 0;
        }

        var shorter = a.Length < b.Length ? a : b;
        var longer = a.Length < b.Length ? b : a;
        int leftIndex = 0, rightIndex = 0;
        var skipped = false;
        while (leftIndex < shorter.Length && rightIndex < longer.Length)
        {
            if (shorter[leftIndex] == longer[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }

            if (skipped) return false;
            skipped = true;
            rightIndex++;
        }

        return true;
    }

    private sealed record KnownWords(string? Path, HashSet<string> Words);

    /// <summary>Words from the repository dictionary plus the <c>.DotSettings</c> custom words.</summary>
    private async Task<KnownWords> LoadAsync(string? dictionaryPath, string solutionDirectory)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? path = dictionaryPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            var candidate = Path.Combine(solutionDirectory, ".visualMCP", "spelling.dic");
            if (File.Exists(candidate)) path = candidate;
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                var word = line.Trim();
                if (word.Length == 0 || word.StartsWith('#')) continue;
                words.Add(word);
            }
        }
        else
        {
            path = null;
        }

        try
        {
            var settings = DotSettingsService.Parse(Directory
                .EnumerateFiles(solutionDirectory, "*.DotSettings*", SearchOption.AllDirectories)
                .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git"))
                .ToList());
            foreach (var word in settings.DictionaryWords) words.Add(word);
        }
        catch (Exception)
        {
            // A missing or unreadable settings folder only means fewer known words.
        }

        return new KnownWords(path, words);
    }

    /// <summary>Identifiers and comment words, with the 1-based line they appear on.</summary>
    private static IEnumerable<(string Word, int Line, string Kind)> Tokens(SyntaxNode root)
    {
        var seenComments = new HashSet<int>();

        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                var line = token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                foreach (var word in SplitIdentifier(token.ValueText))
                    yield return (word, line, "identifier");
            }

            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                if (!IsComment(trivia)) continue;
                // The same comment can sit in the trailing list of one token and the leading list of
                // the next; its span is the identity.
                if (!seenComments.Add(trivia.SpanStart)) continue;

                var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                foreach (Match match in Regex.Matches(trivia.ToString(), "[A-Za-z][A-Za-z']{2,}"))
                    yield return (match.Value, line, "comment");
            }
        }
    }

    /// <summary><c>GetOrderItems</c> → get, order, items; <c>order_items</c> → order, items.</summary>
    public static IEnumerable<string> SplitIdentifier(string identifier)
    {
        var start = 0;
        for (var index = 1; index <= identifier.Length; index++)
        {
            var boundary = index == identifier.Length ||
                           identifier[index] == '_' ||
                           (char.IsUpper(identifier[index]) &&
                            (char.IsLower(identifier[index - 1]) ||
                             (index + 1 < identifier.Length && char.IsLower(identifier[index + 1]))));

            if (!boundary) continue;

            var word = identifier[start..index].Trim('_');
            if (word.Length > 0) yield return word;
            start = index;
        }
    }

    private static bool IsComment(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
}
