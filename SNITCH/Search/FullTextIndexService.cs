using System.Diagnostics;
using System.Text;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Search;

/// <summary>
/// The solution's full-text index: every source, document and decompiled file, one row per line, so a
/// question about its contents is one query instead of a directory walk and a series of file reads.
/// </summary>
public interface IFullTextIndexService
{
    /// <summary>
    /// Indexes the files under the solution (or <paramref name="path"/>) whose length or modification
    /// time changed, drops the documents that no longer exist, and answers with what it did.
    /// </summary>
    Task<object> BuildAsync(string? path = null, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Full-text search: words, phrases, <c>-exclusions</c>, and raw FTS5 syntax when the query uses it.
    /// Answers with <c>file:line</c> and the matching line, plus the files that match most, so no file
    /// has to be opened to find out where something is.
    /// </summary>
    Task<object> SearchAsync(string query, string? glob = null, int limit = 40, int contextLines = 0, CancellationToken cancellationToken = default);

    /// <summary>What the index holds, and why it is empty when it is.</summary>
    object Status();

    /// <summary>The indexed documents, largest first.</summary>
    object Documents(string? kind = null, int limit = 200);

    /// <summary>Indexes one file that was just written (a decompiled type), without a full build.</summary>
    void IndexFile(string path, string kind = "decompiled");

    /// <summary>Indexes text that is about to be written, so a search right after sees it.</summary>
    void IndexText(string path, string kind, string text);
}

public sealed class FullTextIndexService : IFullTextIndexService, IDisposable
{
    /// <summary>Above this a file is a machine-generated artifact, not something to search.</summary>
    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>Hits kept per file, so one repetitive file cannot fill the answer.</summary>
    private const int MaxHitsPerFile = 3;

    /// <summary>Directory names that never hold hand-written content.</summary>
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "TestResults", "artifacts", "packages", ".nuget",
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".razor", ".cshtml",
        ".xaml", ".axaml", ".sql", ".ps1", ".sh", ".cmd", ".bat", ".js", ".mjs", ".ts", ".tsx", ".jsx",
        ".css", ".scss", ".html", ".htm",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".json", ".jsonc", ".yml", ".yaml", ".xml", ".config", ".editorconfig",
        ".resx", ".http", ".csv", ".gitignore", ".gitattributes", ".env",
    };

    private static readonly HashSet<string> DocumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "LICENSE", "NOTICE", ".gitignore", ".gitattributes", ".editorconfig",
        "global.json", "nuget.config",
    };

    /// <summary>Generated files: a search in them only repeats what the generator already states.</summary>
    private static readonly string[] GeneratedSuffixes =
    {
        ".min.js", ".min.css", ".map", ".designer.cs", ".g.cs", ".g.i.cs", ".generated.cs", ".AssemblyInfo.cs",
    };

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;
    private readonly TextIndexStore _index = new();
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    public FullTextIndexService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    // ── build ───────────────────────────────────────────────────────────────

    public async Task<object> BuildAsync(string? path = null, bool force = false, CancellationToken cancellationToken = default)
    {
        var root = Root(path);
        if (root is null) return new { error = "No solution is loaded and none could be discovered." };
        if (!Directory.Exists(root)) return new { error = $"Path not found: {root}" };

        if (!_index.Open(Database(root)))
            return new { error = $"Full-text index unavailable: {_index.UnavailableReason ?? "SQLite could not be opened."}" };

        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        try
        {
            var stamps = _index.Stamps();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexed = 0;
            var refreshed = 0;
            var unchanged = 0;
            var skipped = 0;
            long skippedBytes = 0;

            foreach (var (file, kind) in Collect(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists) continue;
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }

                var full = info.FullName;
                seen.Add(full);

                if (info.Length > MaxFileBytes)
                {
                    skipped++;
                    skippedBytes += info.Length;
                    continue;
                }

                stamps.TryGetValue(full, out var known);
                var modified = info.LastWriteTimeUtc.Ticks;
                if (!force && known is not null && known.Length == info.Length && known.Modified == modified)
                {
                    unchanged++;
                    continue;
                }

                string text;
                try
                {
                    text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                // A NUL byte in the first KB is a binary: indexing it would only add noise.
                if (LooksBinary(text))
                {
                    skipped++;
                    continue;
                }

                var hash = TextIndexStore.ContentHash(text);
                if (!force && known is not null && string.Equals(known.Hash, hash, StringComparison.Ordinal))
                {
                    // Same content, new timestamp (a checkout, a copy): only the stamp moves, the
                    // lines are already stored and re-indexing them would cost the whole file again.
                    await _index.TouchAsync(full, info.Length, modified, cancellationToken).ConfigureAwait(false);
                    refreshed++;
                    continue;
                }

                await _index.ReplaceAsync(
                    full, kind, hash, info.Length, modified, SplitLines(text), cancellationToken).ConfigureAwait(false);
                indexed++;
            }

            // A deleted or renamed file must stop answering. Only a walk of the whole solution can
            // know that: a build of one folder says nothing about the files outside it.
            var removed = 0;
            if (string.Equals(root, Root(null), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var stale in stamps.Keys.Where(known => !seen.Contains(known)))
                {
                    await _index.DeleteAsync(stale, cancellationToken).ConfigureAwait(false);
                    removed++;
                }
            }

            var status = _index.Status();
            started.Stop();

            return new
            {
                root,
                database = _index.DatabasePath,
                indexed,
                refreshed,
                unchanged,
                removed,
                skipped,
                skippedBytes,
                documents = status.Documents,
                lines = status.Lines,
                elapsedMs = started.ElapsedMilliseconds,
                note = status.Documents == 0
                    ? "Nothing was indexed: no source, documentation or decompiled file was found under this root."
                    : null,
            };
        }
        finally
        {
            _buildGate.Release();
        }
    }

    // ── search ──────────────────────────────────────────────────────────────

    public async Task<object> SearchAsync(
        string query, string? glob = null, int limit = 40, int contextLines = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "A query is required." };

        var root = Root(null);
        if (root is null) return new { error = "No solution is loaded and none could be discovered." };
        if (!_index.Open(Database(root)))
            return new { error = $"Full-text index unavailable: {_index.UnavailableReason ?? "SQLite could not be opened."}" };

        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;
        if (contextLines < 0) contextLines = 0;
        if (contextLines > 8) contextLines = 8;

        // A search on an empty index would always answer nothing: index once, then answer.
        var status = _index.Status();
        object? build = null;
        if (status.Documents == 0) build = await BuildAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var (rows, expression, error) = Run(query, glob, limit * 8);
        if (error is not null)
            return new { error = $"The query could not be run: {error}", query, expression };

        if (build is not null) status = _index.Status();
        var files = _index.Files(expression, glob, 15);

        var hits = new List<object>();
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            perFile.TryGetValue(row.Path, out var seen);
            if (seen >= MaxHitsPerFile) continue;
            if (hits.Count >= limit) break;
            perFile[row.Path] = seen + 1;

            object? context = null;
            if (contextLines > 0)
            {
                context = _index
                    .Lines(row.Path, Math.Max(1, row.Line - contextLines), row.Line + contextLines)
                    .Select(line => new { line = line.Line, text = Trim(line.Text, 400) })
                    .ToList();
            }

            hits.Add(new
            {
                file = row.Path,
                line = row.Line,
                text = Trim(row.Text, 400),
                context,
                score = Math.Round(-row.Score, 3),
            });
        }

        return new
        {
            query,
            expression,
            matchCount = rows.Count,
            hits,
            files = files.Select(file => new { file = file.Path, matches = file.Matches }).ToList(),
            index = new { documents = status.Documents, lines = status.Lines, newest = status.Newest },
            built = build is not null ? build : null,
            truncated = rows.Count >= limit * 8 || files.Count >= 15,
            note = hits.Count == 0
                ? "No line matched. The index covers the solution's source, documentation and decompiled files; build it again after large changes."
                : null,
        };
    }

    public object Status()
    {
        var root = Root(null);
        if (root is null) return new { error = "No solution is loaded and none could be discovered." };

        var opened = _index.Open(Database(root));
        var status = _index.Status();
        return new
        {
            available = opened,
            reason = opened ? null : _index.UnavailableReason,
            database = _index.DatabasePath,
            documents = status.Documents,
            lines = status.Lines,
            source = status.Source,
            documentation = status.Documentation,
            decompiled = status.Decompiled,
            newest = status.Newest,
            note = status.Documents == 0 ? "The index is empty: run action=build (a search does it automatically)." : null,
        };
    }

    public object Documents(string? kind = null, int limit = 200)
    {
        var root = Root(null);
        if (root is null) return new { error = "No solution is loaded and none could be discovered." };
        if (!_index.Open(Database(root)))
            return new { error = $"Full-text index unavailable: {_index.UnavailableReason}" };

        var documents = _index.Documents(kind, limit);
        return new
        {
            kind = kind ?? "all",
            count = documents.Count,
            documents = documents.Select(document => new
            {
                file = document.Path,
                kind = document.Kind,
                lines = document.Lines,
                indexedAt = document.IndexedAt,
            }).ToList(),
        };
    }

    // ── single-document writes ──────────────────────────────────────────────

    public void IndexFile(string path, string kind = "decompiled")
    {
        var root = Root(null);
        if (root is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!_index.Open(Database(root))) return;

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) return;

            var text = File.ReadAllText(path);
            IndexText(path, kind, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void IndexText(string path, string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(path) || text is null) return;

        var root = Root(null);
        if (root is null) return;
        if (!_index.Open(Database(root))) return;

        try
        {
            var full = Path.GetFullPath(path);
            _index.ReplaceAsync(
                    full, kind, TextIndexStore.ContentHash(text), text.Length,
                    File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : DateTime.UtcNow.Ticks,
                    SplitLines(text))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ── query building ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs the query: the literal form (every word a prefix phrase, camel-case split the same way the
    /// index splits it) unless the query already uses FTS5 syntax, in which case that is tried first
    /// and the literal form is the fallback for a malformed expression.
    /// </summary>
    private (IReadOnlyList<TextSearchRow> Rows, string Expression, string? Error) Run(string query, string? glob, int fetch)
    {
        var literal = LiteralExpression(query);
        if (literal.Length == 0) return ([], literal, "The query has no words to search for.");

        if (!LooksLikeFullTextSyntax(query))
        {
            var (rows, error) = _index.Search(literal, glob, fetch);
            return (rows, literal, error);
        }

        var (rawRows, rawError) = _index.Search(query, glob, fetch);
        if (rawError is null) return (rawRows, query, null);

        var (fallbackRows, fallbackError) = _index.Search(literal, glob, fetch);
        return (fallbackRows, literal, fallbackError);
    }

    /// <summary>
    /// Words ANDed as prefix phrases, <c>"quoted phrases"</c> kept whole, <c>-word</c> excluded. Each
    /// word is split on its word boundaries, which is what makes <c>required</c> find
    /// <c>GetRequiredService</c>.
    /// </summary>
    public static string LiteralExpression(string query)
    {
        var positives = new List<string>();
        var negatives = new List<string>();

        foreach (var token in Tokens(query))
        {
            if (token.Length == 0) continue;

            if (token.Length > 2 && token[0] is '"' or '\'' && token[^1] == token[0])
            {
                Add(positives, Quote(IdentifierWords.Split(token[1..^1]), prefix: false));
                continue;
            }

            if (token.Length > 1 && token[0] == '-' && !token.StartsWith("--", StringComparison.Ordinal))
            {
                Add(negatives, Quote(IdentifierWords.Split(token[1..]), prefix: true));
                continue;
            }

            Add(positives, Quote(IdentifierWords.Split(token), prefix: true));
        }

        var expression = new StringBuilder();
        if (positives.Count > 0)
        {
            expression.Append(string.Join(" AND ", positives));
        }

        if (negatives.Count > 0)
        {
            if (expression.Length == 0) return string.Join(" OR ", negatives);
            expression.Append(" NOT (").Append(string.Join(" OR ", negatives)).Append(')');
        }

        return expression.ToString();
    }

    private static void Add(List<string> terms, string term)
    {
        if (term.Length > 0) terms.Add(term);
    }

    /// <summary>Splits on whitespace, keeping a quoted phrase (double or single quotes) together.</summary>
    private static IEnumerable<string> Tokens(string query)
    {
        var token = new StringBuilder();
        var quote = '\0';
        foreach (var c in query)
        {
            if (quote != '\0')
            {
                token.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'')
            {
                token.Append(c);
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }

                continue;
            }

            token.Append(c);
        }

        if (token.Length > 0) yield return token.ToString();
    }

    /// <summary>An FTS5 phrase, in double quotes; <paramref name="prefix"/> makes its last word a prefix.</summary>
    private static string Quote(string text, bool prefix)
    {
        var cleaned = text.Replace("\"", " ", StringComparison.Ordinal).Trim();
        if (cleaned.Length == 0) return string.Empty;

        return prefix ? $"\"{cleaned}\"*" : $"\"{cleaned}\"";
    }

    /// <summary>Whether the query is already an FTS5 expression rather than words to search for.</summary>
    private static bool LooksLikeFullTextSyntax(string query) =>
        query.Contains('*', StringComparison.Ordinal) ||
        query.Contains('(', StringComparison.Ordinal) ||
        query.Contains(')', StringComparison.Ordinal) ||
        query.Contains(" NEAR", StringComparison.OrdinalIgnoreCase) ||
        query.Contains(" AND ", StringComparison.OrdinalIgnoreCase) ||
        query.Contains(" OR ", StringComparison.OrdinalIgnoreCase) ||
        query.Contains(" NOT ", StringComparison.OrdinalIgnoreCase);

    // ── walking ─────────────────────────────────────────────────────────────

    private IEnumerable<(string File, string Kind)> Collect(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                children = [];
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, ".visualMCP", StringComparison.OrdinalIgnoreCase))
                {
                    // The store's own decompiled output is searchable; its database and reports are not.
                    var decompiled = Path.Combine(child, "decompiled");
                    if (Directory.Exists(decompiled)) directories.Push(decompiled);
                    continue;
                }

                if (NoiseDirectories.Contains(name)) continue;
                directories.Push(child);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files = [];
            }

            foreach (var file in files)
            {
                if (KindOf(file) is not { } kind) continue;
                yield return (file, kind);
            }
        }
    }

    /// <summary>The kind a file is indexed as, or null when it is not text worth indexing.</summary>
    private static string? KindOf(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var generated in GeneratedSuffixes)
        {
            if (name.EndsWith(generated, StringComparison.OrdinalIgnoreCase)) return null;
        }

        var extension = Path.GetExtension(name);
        if (CodeExtensions.Contains(extension))
            return path.Replace('\\', '/').Contains(".visualMCP/decompiled", StringComparison.OrdinalIgnoreCase)
                ? "decompiled"
                : "source";

        if (DocumentExtensions.Contains(extension) || DocumentNames.Contains(name))
            return "doc";

        return null;
    }

    private static bool LooksBinary(string text)
    {
        var limit = Math.Min(text.Length, 1024);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\0') return true;
        }

        return false;
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>A hit line is cut at a readable length; the file:line is what a caller acts on.</summary>
    private static string Trim(string line, int max)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private string? Root(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

        var solution = _workspace.LoadedSolutionPath ?? _workspace.DiscoverSolutionPath();
        return string.IsNullOrWhiteSpace(solution) ? null : Path.GetDirectoryName(solution);
    }

    private string Database(string root)
    {
        if (_store?.EnsureDatabase() is { } database) return database;

        var directory = Path.Combine(root, ".visualMCP");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "visualmcp.db");
    }

    public void Dispose()
    {
        _index.Dispose();
        _buildGate.Dispose();
    }
}
