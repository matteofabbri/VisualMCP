using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Snitch.Storage.Schema;

namespace Snitch.Search;

/// <summary>One indexed document as the index remembers it: the freshness stamp plus what it cost to store.</summary>
public sealed record IndexedDocument(
    string Path, string Kind, long Length, long Modified, string Hash, int Lines, DateTimeOffset IndexedAt);

/// <summary>How the index is populated, by kind.</summary>
public sealed record TextIndexStatus(
    int Documents, long Lines, int Source, int Documentation, int Decompiled, string? Newest, string? Database);

/// <summary>One matching line, with the sentence the query was looking for.</summary>
public sealed record TextSearchRow(string Path, int Line, string Text, double Score);

/// <summary>One line of context around a hit.</summary>
public sealed record TextContextLine(int Line, string Text);

/// <summary>
/// The full-text index on disk: an FTS5 table inside <c>&lt;solution&gt;/.visualMCP/visualmcp.db</c>,
/// one row per line, so a query answers with <c>file:line</c> instead of a document to open.
/// </summary>
/// <remarks>
/// <para>
/// The indexed column is a copy of the line with identifiers split at their word boundaries
/// (<c>GetRequiredService</c> → <c>Get Required Service</c>), because FTS5's tokenizer sees a camel-cased
/// identifier as one word and would never match <c>required</c>. The split text keeps the line count of
/// the original, so a hit's line number is the real one. The raw line travels in an unindexed column, so
/// a hit needs no read from disk.
/// </para>
/// <para>
/// Like the symbol index it owns its tables and its connection: the store, the board and this index are
/// separate writers of the same file, so every write waits on <c>busy_timeout</c> instead of failing.
/// </para>
/// </remarks>
public sealed class TextIndexStore : IDisposable
{
    /// <summary>The weight of the indexed column in bm25; the payload columns are not searchable.</summary>
    private const string Rank = "bm25(ti_line, 0.0, 0.0, 0.0, 1.0)";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    /// <summary>The database this index reads, or null when it is not open.</summary>
    public string? DatabasePath => _path;

    /// <summary>Why the index is unavailable, or null when it is open or was never opened.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Opens (and creates) the index tables. Returns false when SQLite or FTS5 cannot be used.</summary>
    public bool Open(string databasePath)
    {
        _gate.Wait();
        try
        {
            if (_connection is not null && string.Equals(_path, databasePath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (_unavailable) return false;

            try
            {
                _connection?.Dispose();
                _connection = null;

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);

                var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();

                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
                    pragma.ExecuteNonQuery();
                }

                VisualMcpSchema.Ensure(connection);

                _connection = connection;
                _path = databasePath;
                UnavailableReason = null;
                return true;
            }
            catch (Exception ex)
            {
                // A SQLite build without FTS5 (or a read-only folder) must not break a tool call.
                _unavailable = true;
                UnavailableReason = ex.Message;
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The freshness stamp of every indexed document, so a build can skip the unchanged ones.</summary>
    public Dictionary<string, IndexedDocument> Stamps()
    {
        var stamps = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase);
        var connection = Open();
        if (connection is null) return stamps;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, kind, length, modified, hash, lines, indexed_at FROM ti_document;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var at = DateTimeOffset.TryParse(reader.GetString(6), out var parsed) ? parsed : DateTimeOffset.MinValue;
                stamps[reader.GetString(0)] = new IndexedDocument(
                    reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetString(4), reader.GetInt32(5), at);
            }

            return stamps;
        }
        catch (SqliteException)
        {
            return stamps;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces a document, line by line, in one transaction: the old rows are dropped first, so an
    /// interrupted build leaves either the previous version or the new one, never a mix.
    /// </summary>
    public async Task ReplaceAsync(
        string path, string kind, string hash, long length, long modified,
        IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteRowsAsync(connection, path, cancellationToken).ConfigureAwait(false);

            using var transaction = connection.BeginTransaction();

            // The rowid of an FTS5 row is assigned by the insert, and RETURNING does not report it
            // (it yields -1 for a virtual table), so a document's rows are numbered here: the ids are
            // known before the insert, which is what makes the delete in DeleteRowsAsync exact.
            long first;
            using (var next = connection.CreateCommand())
            {
                next.Transaction = transaction;
                next.CommandText = """
                    SELECT COALESCE((SELECT rowid FROM ti_line ORDER BY rowid DESC LIMIT 1), 0) + 1;
                    """;
                first = (long)next.ExecuteScalar()!;
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ti_line (rowid, path, line, text, words)
                    VALUES ($rowid, $path, $line, $text, $words);
                    """;
                var idParameter = insert.Parameters.Add("$rowid", SqliteType.Integer);
                var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
                var lineParameter = insert.Parameters.Add("$line", SqliteType.Integer);
                var textParameter = insert.Parameters.Add("$text", SqliteType.Text);
                var wordsParameter = insert.Parameters.Add("$words", SqliteType.Text);

                using var row = connection.CreateCommand();
                row.Transaction = transaction;
                row.CommandText = "INSERT INTO ti_row (path, line, rowid) VALUES ($path, $line, $rowid);";
                var rowPath = row.Parameters.Add("$path", SqliteType.Text);
                var rowLine = row.Parameters.Add("$line", SqliteType.Integer);
                var rowId = row.Parameters.Add("$rowid", SqliteType.Integer);

                pathParameter.Value = path;
                rowPath.Value = path;
                for (var index = 0; index < lines.Count; index++)
                {
                    var number = index + 1;
                    var id = first + index;

                    idParameter.Value = id;
                    lineParameter.Value = number;
                    textParameter.Value = lines[index];
                    wordsParameter.Value = IdentifierWords.Split(lines[index]);
                    insert.ExecuteNonQuery();

                    rowLine.Value = number;
                    rowId.Value = id;
                    row.ExecuteNonQuery();
                }
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = """
                    INSERT INTO ti_document (path, kind, length, modified, hash, lines, indexed_at)
                    VALUES ($path, $kind, $length, $modified, $hash, $lines, $at)
                    ON CONFLICT(path) DO UPDATE SET
                        kind = excluded.kind,
                        length = excluded.length,
                        modified = excluded.modified,
                        hash = excluded.hash,
                        lines = excluded.lines,
                        indexed_at = excluded.indexed_at;
                    """;
                stamp.Parameters.AddWithValue("$path", path);
                stamp.Parameters.AddWithValue("$kind", kind);
                stamp.Parameters.AddWithValue("$length", length);
                stamp.Parameters.AddWithValue("$modified", modified);
                stamp.Parameters.AddWithValue("$hash", hash);
                stamp.Parameters.AddWithValue("$lines", lines.Count);
                stamp.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                stamp.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (SqliteException)
        {
            // A document that fails to write is re-indexed next time: its stamp was never replaced.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves a document's freshness stamp without touching its lines: same content, new timestamp.</summary>
    public async Task TouchAsync(string path, long length, long modified, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ti_document SET length = $length, modified = $modified WHERE path = $path;
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$length", length);
            command.Parameters.AddWithValue("$modified", modified);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops one document and its lines.</summary>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteRowsAsync(connection, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Matching lines, best first. The expression is FTS5 syntax over the split-identifier column.</summary>
    public (IReadOnlyList<TextSearchRow> Rows, string? Error) Search(
        string expression, string? glob, int limit)
    {
        var connection = Open();
        if (connection is null) return ([], UnavailableReason);

        if (limit < 1) limit = 1;
        if (limit > 2000) limit = 2000;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT l.path, CAST(l.line AS INTEGER), l.text, {Rank} AS rank
                FROM ti_line l
                WHERE ti_line MATCH $query {(string.IsNullOrWhiteSpace(glob) ? string.Empty : "AND l.path GLOB $glob")}
                ORDER BY rank LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", expression);
            command.Parameters.AddWithValue("$limit", limit);
            if (!string.IsNullOrWhiteSpace(glob)) command.Parameters.AddWithValue("$glob", glob);

            var rows = new List<TextSearchRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TextSearchRow(
                    reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetDouble(3)));
            }

            return (rows, null);
        }
        catch (SqliteException ex)
        {
            return ([], ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many lines match per file, most first — which files are about the query.</summary>
    public IReadOnlyList<(string Path, int Matches)> Files(string expression, string? glob, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT l.path, COUNT(*) AS matches
                FROM ti_line l
                WHERE ti_line MATCH $query {(string.IsNullOrWhiteSpace(glob) ? string.Empty : "AND l.path GLOB $glob")}
                GROUP BY l.path ORDER BY matches DESC, l.path LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", expression);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            if (!string.IsNullOrWhiteSpace(glob)) command.Parameters.AddWithValue("$glob", glob);

            var files = new List<(string, int)>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) files.Add((reader.GetString(0), reader.GetInt32(1)));
            return files;
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The stored lines of one range, so context comes from the index and not from disk.</summary>
    public IReadOnlyList<TextContextLine> Lines(string path, int fromLine, int toLine)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CAST(line AS INTEGER), text FROM ti_line
                WHERE path = $path AND CAST(line AS INTEGER) BETWEEN $from AND $to
                ORDER BY CAST(line AS INTEGER);
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$from", fromLine);
            command.Parameters.AddWithValue("$to", toLine);

            var lines = new List<TextContextLine>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) lines.Add(new TextContextLine(reader.GetInt32(0), reader.GetString(1)));
            return lines;
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What the index holds.</summary>
    public TextIndexStatus Status()
    {
        var connection = Open();
        if (connection is null) return new TextIndexStatus(0, 0, 0, 0, 0, null, _path);

        _gate.Wait();
        try
        {
            var documents = Count(connection, "SELECT COUNT(*) FROM ti_document;");
            var lines = Count(connection, "SELECT COALESCE(SUM(lines), 0) FROM ti_document;");
            var source = Count(connection, "SELECT COUNT(*) FROM ti_document WHERE kind = 'source';");
            var documentation = Count(connection, "SELECT COUNT(*) FROM ti_document WHERE kind = 'doc';");
            var decompiled = Count(connection, "SELECT COUNT(*) FROM ti_document WHERE kind = 'decompiled';");

            string? newest = null;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT MAX(indexed_at) FROM ti_document;";
                if (command.ExecuteScalar() is string value) newest = value;
            }

            return new TextIndexStatus(documents, lines, source, documentation, decompiled, newest, _path);
        }
        catch (SqliteException)
        {
            return new TextIndexStatus(0, 0, 0, 0, 0, null, _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The indexed files, largest first, so a caller can see what a search can reach.</summary>
    public IReadOnlyList<IndexedDocument> Documents(string? kind, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT path, kind, length, modified, hash, lines, indexed_at FROM ti_document
                WHERE ($kind IS NULL OR kind = $kind) ORDER BY lines DESC, path LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20_000));

            var documents = new List<IndexedDocument>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var at = DateTimeOffset.TryParse(reader.GetString(6), out var parsed) ? parsed : DateTimeOffset.MinValue;
                documents.Add(new IndexedDocument(
                    reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetString(4), reader.GetInt32(5), at));
            }

            return documents;
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops everything; the next build indexes from scratch.</summary>
    public void Clear()
    {
        var connection = Open();
        if (connection is null) return;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ti_line; DELETE FROM ti_row; DELETE FROM ti_document;";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    private async Task DeleteRowsAsync(SqliteConnection connection, string path, CancellationToken cancellationToken)
    {
        using (var lines = connection.CreateCommand())
        {
            lines.CommandText = "DELETE FROM ti_line WHERE rowid IN (SELECT rowid FROM ti_row WHERE path = $path);";
            lines.Parameters.AddWithValue("$path", path);
            await lines.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var table in new[] { "ti_row", "ti_document" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE path = $path;";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SqliteConnection? Open()
    {
        _gate.Wait();
        try
        {
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>The content hash every part of the store uses, so a stamp means the same thing everywhere.</summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

/// <summary>
/// Splits a line into the words a search is written with: <c>GetRequiredService</c> becomes
/// <c>Get Required Service</c> and <c>ILSpy</c> becomes <c>IL Spy</c>, while the line count stays
/// exactly the same (only spaces are inserted), so a match's line number is still the real one.
/// </summary>
public static class IdentifierWords
{
    public static string Split(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        // Most lines need no work; scanning is cheap but allocating the builder is not.
        var needsSplit = false;
        for (var i = 1; i < line.Length; i++)
        {
            if (char.IsUpper(line[i]) && (char.IsLower(line[i - 1]) || char.IsDigit(line[i - 1])))
            {
                needsSplit = true;
                break;
            }

            if (char.IsUpper(line[i]) && char.IsUpper(line[i - 1]) && i + 1 < line.Length && char.IsLower(line[i + 1]))
            {
                needsSplit = true;
                break;
            }
        }

        if (!needsSplit) return line;

        var text = new StringBuilder(line.Length + 16);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (i > 0 && char.IsUpper(c))
            {
                var previous = line[i - 1];
                var nextLower = i + 1 < line.Length && char.IsLower(line[i + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextLower))
                    text.Append(' ');
            }

            text.Append(c);
        }

        return text.ToString();
    }
}
