using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Snitch.Storage.Schema;

namespace Snitch.Metacode.Indexing;

/// <summary>
/// The solution's metacode in <c>.visualMCP/visualmcp.db</c>: one row per document (the freshness token
/// is its content hash plus its timestamp) and one row per declaration, holding the canonical
/// pseudocode and the structural facts computed over the model.
/// </summary>
/// <remarks>
/// A document is stored twice, on purpose. <c>source_document</c> keeps the file as its own language
/// writes it — the native side, which exists for every document in the solution; <c>mc_document</c> and
/// <c>mc_tree</c> keep what Snitch's model made of it, which exists only for the languages a frontend can
/// read. A file in a language with no reader is therefore present in the store and visibly unread,
/// rather than missing.
/// </remarks>
/// <remarks>
/// It owns its connection, like the symbol index and the board that share the same file, and every
/// operation degrades to a no-op when SQLite cannot be used: the index is an optimisation, never a
/// dependency. The tree text is gzip-compressed because a type's text repeats its members, each of which
/// also has a row of its own.
/// </remarks>
public sealed class MetacodeIndexStore : IDisposable
{
    private const string TreeColumns = """
        declaration_key, path, kind, name, line, col, end_line, cyclomatic, cognitive,
        max_nesting, statements, branches, expressions, text
        """;

    /// <summary>
    /// The path prefix of a document that has no file: the metacode of a decompiled assembly. Those rows
    /// live in the same tables as the solution's own code (so one query sees both), and the prefix is what
    /// tells a refresh not to prune them — they are not documents of the solution and never will be.
    /// </summary>
    public const string DecompiledPrefix = "assembly://";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    /// <summary>The database the index is reading, or null when it is not open.</summary>
    public string? Path => _path;

    /// <summary>The content hash a document is stamped with; the same function the other stores use.</summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Opens the database, creating the metacode tables. Returns false when SQLite cannot be used.</summary>
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
                    // Other owners of the same file (the store, the board, the symbol index) write too:
                    // wait for them instead of failing the query.
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                VisualMcpSchema.Ensure(connection);

                _connection = connection;
                _path = databasePath;
                return true;
            }
            catch (Exception)
            {
                _unavailable = true;
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every indexed path with its freshness token, so a refresh can skip what did not change.</summary>
    public Dictionary<string, MetacodeDocumentState> Known()
    {
        var known = new Dictionary<string, MetacodeDocumentState>(StringComparer.OrdinalIgnoreCase);
        var connection = Open();
        if (connection is null) return known;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.path, m.hash, m.mtime, CASE WHEN d.path IS NULL THEN 0 ELSE 1 END
                FROM mc_document m LEFT JOIN source_document d ON d.path = m.path
                UNION ALL
                SELECT s.path, s.hash, s.mtime, 1
                FROM source_document s
                WHERE s.path NOT IN (SELECT path FROM mc_document);
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                known[reader.GetString(0)] = new MetacodeDocumentState(reader.GetString(1), reader.GetInt64(2))
                {
                    HasSource = reader.GetInt64(3) != 0,
                };
            }
            return known;
        }
        catch (SqliteException)
        {
            return known;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes one document's rows, replacing whatever that path held before. Null when the database is unavailable.</summary>
    public async Task<bool> ReplaceAsync(MetacodeDocumentFacts facts, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = connection.BeginTransaction();

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM mc_tree WHERE path = $path;";
                delete.Parameters.AddWithValue("$path", facts.Path);
                delete.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"""
                    INSERT OR REPLACE INTO mc_tree
                        ({TreeColumns}, doc_hash)
                    VALUES ($key, $path, $kind, $name, $line, $col, $end, $cc, $cog,
                            $nesting, $statements, $branches, $expressions, $text, $hash);
                    """;

                var key = insert.Parameters.Add("$key", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var kind = insert.Parameters.Add("$kind", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var line = insert.Parameters.Add("$line", SqliteType.Integer);
                var col = insert.Parameters.Add("$col", SqliteType.Integer);
                var end = insert.Parameters.Add("$end", SqliteType.Integer);
                var cc = insert.Parameters.Add("$cc", SqliteType.Integer);
                var cog = insert.Parameters.Add("$cog", SqliteType.Integer);
                var nesting = insert.Parameters.Add("$nesting", SqliteType.Integer);
                var statements = insert.Parameters.Add("$statements", SqliteType.Integer);
                var branches = insert.Parameters.Add("$branches", SqliteType.Integer);
                var expressions = insert.Parameters.Add("$expressions", SqliteType.Integer);
                var text = insert.Parameters.Add("$text", SqliteType.Blob);
                var hash = insert.Parameters.Add("$hash", SqliteType.Text);

                foreach (var row in facts.Rows)
                {
                    key.Value = row.Key;
                    path.Value = row.Path;
                    kind.Value = row.Kind;
                    name.Value = row.Name;
                    line.Value = row.Line;
                    col.Value = row.Column;
                    end.Value = row.EndLine;
                    cc.Value = row.Cyclomatic;
                    cog.Value = row.Cognitive;
                    nesting.Value = row.MaxNesting;
                    statements.Value = row.Statements;
                    branches.Value = row.Branches;
                    expressions.Value = row.Expressions;
                    text.Value = Compress(row.Text);
                    hash.Value = facts.Hash;
                    insert.ExecuteNonQuery();
                }
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = """
                    INSERT OR REPLACE INTO mc_document
                        (path, project, hash, mtime, language, declarations, nodes, diagnostics, indexed_at)
                    VALUES ($path, $project, $hash, $mtime, $language, $declarations, $nodes, $diagnostics, $at);
                    """;
                stamp.Parameters.AddWithValue("$path", facts.Path);
                stamp.Parameters.AddWithValue("$project", facts.Project);
                stamp.Parameters.AddWithValue("$hash", facts.Hash);
                stamp.Parameters.AddWithValue("$mtime", facts.Mtime);
                stamp.Parameters.AddWithValue("$language", facts.Language);
                stamp.Parameters.AddWithValue("$declarations", facts.Rows.Count);
                stamp.Parameters.AddWithValue("$nodes", facts.Rows.Sum(row => row.Statements + row.Expressions));
                stamp.Parameters.AddWithValue("$diagnostics", facts.Diagnostics);
                stamp.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                stamp.ExecuteNonQuery();
            }

            // The native half of the record: the file as its own language writes it, next to the tree
            // the model produced from it. One transaction, so the two never disagree about a file.
            if (facts.Source is not null)
                WriteSource(connection, transaction, facts);

            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes a document the model cannot read: its native source and nothing else. Any metacode rows the
    /// path still holds are dropped, so "this language has no reader" is a state the store records
    /// instead of a gap it hides.
    /// </summary>
    public async Task<bool> ReplaceSourceAsync(MetacodeDocumentFacts facts, CancellationToken cancellationToken = default)
    {
        if (facts.Source is null) return false;

        var connection = Open();
        if (connection is null) return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = connection.BeginTransaction();

            foreach (var table in new[] { "mc_tree", "mc_document", "source_document" })
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE path = $path;";
                delete.Parameters.AddWithValue("$path", facts.Path);
                delete.ExecuteNonQuery();
            }

            WriteSource(connection, transaction, facts);
            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void WriteSource(
        SqliteConnection connection, SqliteTransaction transaction, MetacodeDocumentFacts facts)
    {
        var source = facts.Source!;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO source_document
                (path, project, language, hash, mtime, chars, lines, text, indexed_at)
            VALUES ($path, $project, $language, $hash, $mtime, $chars, $lines, $text, $at);
            """;
        command.Parameters.AddWithValue("$path", facts.Path);
        command.Parameters.AddWithValue("$project", facts.Project);
        command.Parameters.AddWithValue("$language", facts.Language);
        command.Parameters.AddWithValue("$hash", facts.Hash);
        command.Parameters.AddWithValue("$mtime", facts.Mtime);
        command.Parameters.AddWithValue("$chars", source.Length);
        command.Parameters.AddWithValue("$lines", LineCount(source));
        command.Parameters.AddWithValue("$text", source);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>How many lines the text spans: a count of separators, not a split, because it is only a column.</summary>
    private static int LineCount(string text)
    {
        var lines = 1;
        foreach (var character in text)
            if (character == '\n') lines++;
        return lines;
    }

    /// <summary>
    /// Renews a document's timestamp without touching its rows: the file was written again and the
    /// content hash says nothing changed, so the next pass can skip it on the timestamp alone. It stamps
    /// both halves — a document with no reader has only the native row, and would otherwise be re-read on
    /// every pass.
    /// </summary>
    public void Touch(string path, long mtime)
    {
        var connection = Open();
        if (connection is null || mtime == 0) return;

        _gate.Wait();
        try
        {
            foreach (var table in new[] { "mc_document", "source_document" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE {table} SET mtime = $mtime WHERE path = $path;";
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$mtime", mtime);
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
        {
            // The stamp is an optimisation; a failed update only costs one more hash next time.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a document that is no longer part of the solution. Returns how many were removed.</summary>
    public async Task<int> RemoveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return 0;

        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = connection.BeginTransaction();
            var removed = 0;

            foreach (var path in paths)
            {
                foreach (var table in new[] { "mc_tree", "mc_document", "source_document" })
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {table} WHERE path = $path;";
                    delete.Parameters.AddWithValue("$path", path);
                    removed += delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return removed;
        }
        catch (SqliteException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops every path the solution no longer contains. Returns how many documents were removed.</summary>
    public async Task<int> PruneAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        var stale = new List<string>();
        foreach (var path in Known().Keys)
        {
            if (!paths.Contains(path)) stale.Add(path);
        }

        if (stale.Count == 0) return 0;

        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = connection.BeginTransaction();
            foreach (var path in stale)
            {
                foreach (var table in new[] { "mc_tree", "mc_document", "source_document" })
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {table} WHERE path = $path;";
                    delete.Parameters.AddWithValue("$path", path);
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return stale.Count;
        }
        catch (SqliteException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops every document under <paramref name="prefix"/> that is not in <paramref name="keep"/>: the
    /// types an assembly no longer contains after it was decompiled again. Returns how many documents were
    /// removed.
    /// </summary>
    public async Task<int> RemoveUnderAsync(
        string prefix, IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stale = new List<string>();
            using (var query = connection.CreateCommand())
            {
                // substr, not LIKE: an assembly name can contain '_' or '%', which LIKE would treat as wildcards.
                query.CommandText = "SELECT path FROM mc_document WHERE substr(path, 1, length($prefix)) = $prefix;";
                query.Parameters.AddWithValue("$prefix", prefix);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    if (!keep.Contains(reader.GetString(0), StringComparer.OrdinalIgnoreCase))
                        stale.Add(reader.GetString(0));
                }
            }

            if (stale.Count == 0) return 0;

            using var transaction = connection.BeginTransaction();
            foreach (var path in stale)
            {
                foreach (var table in new[] { "mc_tree", "mc_document", "source_document" })
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {table} WHERE path = $path;";
                    delete.Parameters.AddWithValue("$path", path);
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return stale.Count;
        }
        catch (SqliteException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The content hash stored for the first document of a decompiled assembly — the assembly's MVID —
    /// or null when the store has never seen that assembly. It is the freshness token a re-decompilation
    /// compares against, so an unchanged binary is not decompiled twice.
    /// </summary>
    public string? DecompiledHash(string assemblyFileName)
    {
        var connection = Open();
        if (connection is null) return null;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT hash FROM mc_document
                WHERE substr(path, 1, length($prefix)) = $prefix
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$prefix", DecompiledPrefix + assemblyFileName + "/");
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Which binaries the store holds code for: the file name, the MVID the rows are keyed on, and how
    /// many types each one contributed. It is what makes a decompiled assembly discoverable — otherwise
    /// the only way to learn what is there would be to guess a type name.
    /// </summary>
    public IReadOnlyList<DecompiledAssemblyRow> DecompiledAssemblies()
    {
        var found = new Dictionary<string, (string Mvid, int Types)>(StringComparer.OrdinalIgnoreCase);

        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT path, hash FROM mc_document
                WHERE substr(path, 1, length($prefix)) = $prefix
                ORDER BY path;
                """;
            command.Parameters.AddWithValue("$prefix", DecompiledPrefix);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var rest = path[DecompiledPrefix.Length..];

                // assembly://<file>/<type> — the file name is everything before the first '/'.
                var slash = rest.IndexOf('/');
                if (slash <= 0) continue;

                var assembly = rest[..slash];
                var mvid = reader.GetString(1);
                found[assembly] = found.TryGetValue(assembly, out var existing)
                    ? (mvid, existing.Types + 1)
                    : (mvid, 1);
            }

            return found
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new DecompiledAssemblyRow(entry.Key, entry.Value.Mvid, entry.Value.Types))
                .ToList();
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

    /// <summary>The stored rows of one file, in source order.</summary>
    public IReadOnlyList<MetacodeTreeRow> InPath(string path, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {TreeColumns} FROM mc_tree WHERE path = $path ORDER BY line, col LIMIT $limit;";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$limit", Clamp(limit));
            return Read(command);
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

    /// <summary>Rows whose declaration key or name matches exactly; a qualified name matches the end of the key.</summary>
    public IReadOnlyList<MetacodeTreeRow> Resolve(string query, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {TreeColumns} FROM mc_tree
                WHERE declaration_key = $q OR name = $q OR declaration_key LIKE '%' || $q || '%'
                ORDER BY line, col LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$q", query);
            command.Parameters.AddWithValue("$limit", Clamp(limit));
            return Read(command);
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

    /// <summary>The callables, worst first — the complexity report.</summary>
    public IReadOnlyList<MetacodeTreeRow> Complexity(string? project, string? kind, int minCyclomatic, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {TreeColumns} FROM mc_tree
                WHERE cyclomatic >= $min
                  AND kind IN ('method', 'constructor', 'destructor', 'operator', 'function', 'local-function', 'property', 'indexer')
                  AND ($project IS NULL OR path IN (SELECT path FROM mc_document WHERE project = $project))
                  AND ($kind IS NULL OR kind = $kind)
                ORDER BY cyclomatic DESC, cognitive DESC, name, line
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$min", minCyclomatic);
            command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Clamp(limit));
            return Read(command);
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

    public MetacodeIndexStatus Status()
    {
        var connection = Open();
        if (connection is null) return new MetacodeIndexStatus(0, 0, 0, 0, 0, 0, null, _path, null);

        _gate.Wait();
        try
        {
            var documents = Count(connection, "SELECT COUNT(*) FROM mc_document;");
            var declarations = Count(connection, "SELECT COUNT(*) FROM mc_tree;");
            var functions = Count(connection,
                "SELECT COUNT(*) FROM mc_tree WHERE kind IN ('method', 'constructor', 'destructor', 'operator', 'function', 'local-function');");
            var types = Count(connection,
                "SELECT COUNT(*) FROM mc_tree WHERE kind IN ('class', 'struct', 'interface', 'enum', 'record', 'union', 'trait');");
            var max = Count(connection, "SELECT COALESCE(MAX(cyclomatic), 0) FROM mc_tree;");
            var average = Count(connection,
                "SELECT COALESCE(CAST(AVG(cyclomatic) AS INTEGER), 0) FROM mc_tree WHERE cyclomatic > 0;");
            var sources = Count(connection, "SELECT COUNT(*) FROM source_document;");

            string? newest = null;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT MAX(indexed_at) FROM mc_document;";
                if (command.ExecuteScalar() is string value) newest = value;
            }

            return new MetacodeIndexStatus(
                documents, declarations, functions, types, max, average, newest, _path, null)
            {
                Sources = sources,
            };
        }
        catch (SqliteException)
        {
            return new MetacodeIndexStatus(0, 0, 0, 0, 0, 0, null, _path, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many declarations the store holds for one path — used by the tests to prove an edit replaced them.</summary>
    public int CountInPath(string path)
    {
        var connection = Open();
        if (connection is null) return 0;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM mc_tree WHERE path = $path;";
            command.Parameters.AddWithValue("$path", path);
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The native text stored for a path, with the language tag it was read as. This half of the record
    /// exists for every document of the solution, including the ones no frontend could read.
    /// </summary>
    public (string Language, string Text)? SourceOf(string path)
    {
        var connection = Open();
        if (connection is null) return null;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT language, text FROM source_document WHERE path = $path;";
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
        }
        catch (SqliteException)
        {
            return null;
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

    private SqliteConnection? Open() => _connection;

    private static IReadOnlyList<MetacodeTreeRow> Read(SqliteCommand command)
    {
        var rows = new List<MetacodeTreeRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MetacodeTreeRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                Decompress((byte[])reader[13])));
        }

        return rows;
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int Clamp(int limit) => limit <= 0 ? 1 : Math.Min(limit, 5000);

    /// <summary>
    /// The pseudocode of a type repeats its members, and every member has its own row too: compressed,
    /// the duplication costs a fraction of the space it would as text.
    /// </summary>
    private static byte[] Compress(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    private static string Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
