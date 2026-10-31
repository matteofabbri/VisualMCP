using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Snitch.Storage.Schema;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// The symbol index on disk: three tables inside <c>&lt;solution&gt;/.visualMCP/visualmcp.db</c> — the
/// declarations, the uses and the fingerprint of each indexed document. It owns its own connection and
/// its own tables, like the board does, and every fact in it is rebuildable from the source files: the
/// files on disk stay the only truth.
/// </summary>
/// <remarks>
/// Writes replace one document at a time, in one transaction, so a build interrupted halfway leaves
/// the documents it finished valid and the rest to be indexed again on the next run.
/// </remarks>
public sealed class SymbolIndexStore : IDisposable
{
    private const string DeclarationColumns = """
        symbol_key, name, display, signature, kind, accessibility, modifiers, container_key, container_name,
        namespace, is_static, is_abstract, is_virtual, is_override, is_extension, arity, summary, project,
        path, start, length, line, col, end_line, end_col, doc_hash
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    /// <summary>The database the index is reading, or null when it is not open.</summary>
    public string? Path => _path;

    /// <summary>The content hash a document is stamped with; the same function the edit history uses.</summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Opens the database, creating the index tables. Returns false when SQLite cannot be used.</summary>
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
                    // Other owners of the same file (the store, the board) write too: wait for them
                    // instead of failing the query.
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

    /// <summary>The stored hash of every indexed document, so a build can skip the unchanged ones.</summary>
    public Dictionary<string, string> Hashes()
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var connection = Open();
        if (connection is null) return hashes;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, hash FROM si_document;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) hashes[reader.GetString(0)] = reader.GetString(1);
            return hashes;
        }
        catch (SqliteException)
        {
            return hashes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces every row of one document: the declarations, the uses and the fingerprint.</summary>
    public async Task ReplaceAsync(SymbolDocumentFacts facts, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = connection.BeginTransaction();

            foreach (var table in new[] { "si_symbol", "si_usage", "si_metacode" })
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE path = $path;";
                delete.Parameters.AddWithValue("$path", facts.Path);
                delete.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO si_symbol
                        (symbol_key, name, display, signature, kind, accessibility, modifiers, container_key,
                         container_name, namespace, is_static, is_abstract, is_virtual, is_override,
                         is_extension, arity, summary, project, path, start, length, line, col, end_line,
                         end_col, doc_hash)
                    VALUES ($key, $name, $display, $signature, $kind, $accessibility, $modifiers, $container,
                            $containerName, $namespace, $static, $abstract, $virtual, $override, $extension,
                            $arity, $summary, $project, $path, $start, $length, $line, $col, $endLine,
                            $endCol, $hash);
                    """;
                var key = insert.Parameters.Add("$key", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var display = insert.Parameters.Add("$display", SqliteType.Text);
                var signature = insert.Parameters.Add("$signature", SqliteType.Text);
                var kind = insert.Parameters.Add("$kind", SqliteType.Text);
                var accessibility = insert.Parameters.Add("$accessibility", SqliteType.Text);
                var modifiers = insert.Parameters.Add("$modifiers", SqliteType.Text);
                var container = insert.Parameters.Add("$container", SqliteType.Text);
                var containerName = insert.Parameters.Add("$containerName", SqliteType.Text);
                var space = insert.Parameters.Add("$namespace", SqliteType.Text);
                var isStatic = insert.Parameters.Add("$static", SqliteType.Integer);
                var isAbstract = insert.Parameters.Add("$abstract", SqliteType.Integer);
                var isVirtual = insert.Parameters.Add("$virtual", SqliteType.Integer);
                var isOverride = insert.Parameters.Add("$override", SqliteType.Integer);
                var isExtension = insert.Parameters.Add("$extension", SqliteType.Integer);
                var arity = insert.Parameters.Add("$arity", SqliteType.Integer);
                var summary = insert.Parameters.Add("$summary", SqliteType.Text);
                var project = insert.Parameters.Add("$project", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var start = insert.Parameters.Add("$start", SqliteType.Integer);
                var length = insert.Parameters.Add("$length", SqliteType.Integer);
                var line = insert.Parameters.Add("$line", SqliteType.Integer);
                var col = insert.Parameters.Add("$col", SqliteType.Integer);
                var endLine = insert.Parameters.Add("$endLine", SqliteType.Integer);
                var endCol = insert.Parameters.Add("$endCol", SqliteType.Integer);
                var hash = insert.Parameters.Add("$hash", SqliteType.Text);

                foreach (var declaration in facts.Declarations)
                {
                    key.Value = declaration.Key;
                    name.Value = declaration.Name;
                    display.Value = declaration.Display;
                    signature.Value = declaration.Signature;
                    kind.Value = declaration.Kind;
                    accessibility.Value = declaration.Accessibility;
                    modifiers.Value = declaration.Modifiers;
                    container.Value = declaration.ContainerKey;
                    containerName.Value = declaration.ContainerName;
                    space.Value = declaration.Namespace;
                    isStatic.Value = declaration.IsStatic ? 1 : 0;
                    isAbstract.Value = declaration.IsAbstract ? 1 : 0;
                    isVirtual.Value = declaration.IsVirtual ? 1 : 0;
                    isOverride.Value = declaration.IsOverride ? 1 : 0;
                    isExtension.Value = declaration.IsExtension ? 1 : 0;
                    arity.Value = declaration.Arity;
                    summary.Value = (object?)declaration.Summary ?? DBNull.Value;
                    project.Value = declaration.Project;
                    path.Value = declaration.Path;
                    start.Value = declaration.Start;
                    length.Value = declaration.Length;
                    line.Value = declaration.Line;
                    col.Value = declaration.Column;
                    endLine.Value = declaration.EndLine;
                    endCol.Value = declaration.EndColumn;
                    hash.Value = declaration.DocumentHash;
                    insert.ExecuteNonQuery();
                }
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO si_usage (symbol_key, name, kind, container_key, path, start, length, line, col, doc_hash)
                    VALUES ($key, $name, $kind, $container, $path, $start, $length, $line, $col, $hash);
                    """;
                var key = insert.Parameters.Add("$key", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var kind = insert.Parameters.Add("$kind", SqliteType.Text);
                var container = insert.Parameters.Add("$container", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var start = insert.Parameters.Add("$start", SqliteType.Integer);
                var length = insert.Parameters.Add("$length", SqliteType.Integer);
                var line = insert.Parameters.Add("$line", SqliteType.Integer);
                var col = insert.Parameters.Add("$col", SqliteType.Integer);
                var hash = insert.Parameters.Add("$hash", SqliteType.Text);

                foreach (var usage in facts.Usages)
                {
                    key.Value = usage.SymbolKey;
                    name.Value = usage.Name;
                    kind.Value = usage.Kind;
                    container.Value = usage.ContainerKey;
                    path.Value = usage.Path;
                    start.Value = usage.Start;
                    length.Value = usage.Length;
                    line.Value = usage.Line;
                    col.Value = usage.Column;
                    hash.Value = usage.DocumentHash;
                    insert.ExecuteNonQuery();
                }
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = """
                    INSERT INTO si_document (path, project, hash, declarations, usages, indexed_at)
                    VALUES ($path, $project, $hash, $declarations, $usages, $at)
                    ON CONFLICT(path) DO UPDATE SET
                        project = excluded.project,
                        hash = excluded.hash,
                        declarations = excluded.declarations,
                        usages = excluded.usages,
                        indexed_at = excluded.indexed_at;
                    """;
                stamp.Parameters.AddWithValue("$path", facts.Path);
                stamp.Parameters.AddWithValue("$project", facts.Project);
                stamp.Parameters.AddWithValue("$hash", facts.Hash);
                stamp.Parameters.AddWithValue("$declarations", facts.Declarations.Count);
                stamp.Parameters.AddWithValue("$usages", facts.Usages.Count);
                stamp.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                stamp.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO si_metacode (symbol_key, path, kind, name, line, text, doc_hash)
                    VALUES ($key, $path, $kind, $name, $line, $text, $hash);
                    """;
                var key = insert.Parameters.Add("$key", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var kind = insert.Parameters.Add("$kind", SqliteType.Text);
                var name = insert.Parameters.Add("$name", SqliteType.Text);
                var line = insert.Parameters.Add("$line", SqliteType.Integer);
                var text = insert.Parameters.Add("$text", SqliteType.Blob);
                var hash = insert.Parameters.Add("$hash", SqliteType.Text);

                foreach (var tree in facts.Trees)
                {
                    key.Value = tree.SymbolKey;
                    path.Value = tree.Path;
                    kind.Value = tree.Kind;
                    name.Value = tree.Name;
                    line.Value = tree.Line;
                    text.Value = Compress(tree.Text);
                    hash.Value = tree.DocumentHash;
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch (SqliteException)
        {
            // A document that fails to write is re-indexed next time: its hash was never stamped.
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── queries ─────────────────────────────────────────────────────────────

    /// <summary>Declarations by symbol name, optionally a substring search and/or one kind.</summary>
    public IReadOnlyList<SymbolDeclaration> DeclarationsByName(string name, bool partial, string? kind, int limit) =>
        Declarations(
            "($partial = 0 AND name = $name) OR ($partial = 1 AND name LIKE $pattern ESCAPE '\\')",
            name,
            partial,
            kind,
            limit,
            "ORDER BY name, path, start");

    /// <summary>Every declaration of one key (a partial type has more than one).</summary>
    public IReadOnlyList<SymbolDeclaration> DeclarationsByKey(string key, int limit = 50) =>
        Declarations("symbol_key = $name", key, false, null, limit, "ORDER BY path, start");

    public IReadOnlyList<SymbolDeclaration> DeclarationsByPath(string path, int limit) =>
        Declarations("path = $name", path, false, null, limit, "ORDER BY start");

    public IReadOnlyList<SymbolDeclaration> DeclarationsInContainer(string containerKey, int limit) =>
        Declarations("container_key = $name", containerKey, false, null, limit, "ORDER BY start");

    public IReadOnlyList<SymbolUsage> UsagesOf(string symbolKey, string? kind, int limit) =>
        Usages(
            "WHERE symbol_key = $key AND ($kind IS NULL OR kind = $kind) ORDER BY path, line, col",
            symbolKey, kind, limit);

    /// <summary>The uses of a symbol grouped by the member they happen inside — VS's "find all references", grouped.</summary>
    public IReadOnlyList<SymbolContainerCount> ContainersOf(string symbolKey, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT container_key, COUNT(*) AS uses, GROUP_CONCAT(DISTINCT kind) AS kinds
                FROM si_usage
                WHERE symbol_key = $key AND kind NOT IN ('override', 'implement')
                GROUP BY container_key ORDER BY uses DESC, container_key LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$key", symbolKey);
            command.Parameters.AddWithValue("$limit", Clamp(limit));

            var counts = new List<SymbolContainerCount>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                counts.Add(new SymbolContainerCount(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
            return counts;
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

    /// <summary>What a member uses, grouped by the used symbol — the other half of the call hierarchy.</summary>
    public IReadOnlyList<SymbolTargetCount> TargetsOf(string containerKey, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT symbol_key, name, COUNT(*) AS uses, GROUP_CONCAT(DISTINCT kind) AS kinds
                FROM si_usage
                WHERE container_key = $key AND kind <> 'override'
                GROUP BY symbol_key ORDER BY uses DESC, name LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$key", containerKey);
            command.Parameters.AddWithValue("$limit", Clamp(limit));

            var counts = new List<SymbolTargetCount>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                counts.Add(new SymbolTargetCount(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
            return counts;
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

    /// <summary>The kinds a symbol is used in, with how often each occurs.</summary>
    public IReadOnlyList<SymbolKindCount> UsageCounts(string symbolKey)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind, COUNT(*) AS uses FROM si_usage WHERE symbol_key = $key
                GROUP BY kind ORDER BY uses DESC, kind;
                """;
            command.Parameters.AddWithValue("$key", symbolKey);

            var counts = new List<SymbolKindCount>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) counts.Add(new SymbolKindCount(reader.GetString(0), reader.GetInt32(1)));
            return counts;
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

    /// <summary>Total uses per symbol key, for a list of keys in one query (the counts a search shows).</summary>
    public Dictionary<string, int> UsageTotals(IReadOnlyList<string> keys)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        if (keys.Count == 0) return totals;

        var connection = Open();
        if (connection is null) return totals;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            var names = new List<string>(keys.Count);
            for (var index = 0; index < keys.Count; index++)
            {
                var parameter = "$k" + index;
                names.Add(parameter);
                command.Parameters.AddWithValue(parameter, keys[index]);
            }

            command.CommandText = $"""
                SELECT symbol_key, COUNT(*) FROM si_usage
                WHERE kind NOT IN ('override', 'implement') AND symbol_key IN ({string.Join(", ", names)})
                GROUP BY symbol_key;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read()) totals[reader.GetString(0)] = reader.GetInt32(1);
            return totals;
        }
        catch (SqliteException)
        {
            return totals;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One side of a relationship: the types a type extends or implements, and — with
    /// <paramref name="direction"/> incoming — the types that extend it and the members that implement it.
    /// </summary>
    public IReadOnlyList<(string Key, string Name)> Relationship(
        string key, string kind, bool incoming, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = incoming
                ? """
                  SELECT DISTINCT container_key, '' FROM si_usage
                  WHERE symbol_key = $key AND kind = $kind ORDER BY container_key LIMIT $limit;
                  """
                : """
                  SELECT DISTINCT symbol_key, name FROM si_usage
                  WHERE container_key = $key AND kind = $kind ORDER BY name, symbol_key LIMIT $limit;
                  """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$limit", Clamp(limit));

            var edges = new List<(string, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) edges.Add((reader.GetString(0), reader.GetString(1)));
            return edges;
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

    /// <summary>The metacode tree of one declaration, or none when it has no body the model can render.</summary>
    public IReadOnlyList<SymbolMetacode> MetacodeOf(string symbolKey, int limit = 5) =>
        Metacode("symbol_key = $key ORDER BY path LIMIT $limit", symbolKey, limit);

    /// <summary>Every tree the index holds for a file, in source order: the file as metacode.</summary>
    public IReadOnlyList<SymbolMetacode> MetacodeInPath(string path, int limit = 200) =>
        Metacode("path = $key ORDER BY line LIMIT $limit", path, limit);

    /// <summary>Non-public declarations nothing in the index uses.</summary>
    public IReadOnlyList<SymbolDeclaration> Unused(int limit, string? kind = null)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {DeclarationColumns} FROM si_symbol
                WHERE accessibility IN ('private', 'internal', 'protected', 'private protected', 'protected internal')
                  AND ($kind IS NULL OR kind = $kind)
                  AND NOT EXISTS (SELECT 1 FROM si_usage u WHERE u.symbol_key = si_symbol.symbol_key)
                ORDER BY path, start LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Clamp(limit));
            return ReadDeclarations(command);
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

    /// <summary>
    /// Drops the rows of documents the solution no longer contains, so a deleted or renamed file does
    /// not keep answering queries. Returns how many documents were pruned.
    /// </summary>
    public async Task<int> PruneAsync(IReadOnlySet<string> live, CancellationToken cancellationToken = default)
    {
        var connection = Open();
        if (connection is null) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stale = new List<string>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT path FROM si_document;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var path = reader.GetString(0);
                    if (!live.Contains(path)) stale.Add(path);
                }
            }

            foreach (var path in stale)
            {
                foreach (var table in new[] { "si_symbol", "si_usage", "si_metacode", "si_document" })
                {
                    using var delete = connection.CreateCommand();
                    delete.CommandText = $"DELETE FROM {table} WHERE path = $path;";
                    delete.Parameters.AddWithValue("$path", path);
                    delete.ExecuteNonQuery();
                }
            }

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

    /// <summary>How many documents, declarations and uses are stored, plus the histogram of kinds.</summary>
    public SymbolIndexStatus Status()
    {
        var connection = Open();
        if (connection is null)
            return new SymbolIndexStatus(0, 0, 0, 0, null, [], []);

        _gate.Wait();
        try
        {
            var documents = Count(connection, "SELECT COUNT(*) FROM si_document;");
            var symbols = Count(connection, "SELECT COUNT(DISTINCT symbol_key) FROM si_symbol;");
            var declarations = Count(connection, "SELECT COUNT(*) FROM si_symbol;");
            var usages = Count(connection, "SELECT COUNT(*) FROM si_usage;");

            string? newest = null;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT MAX(indexed_at) FROM si_document;";
                if (command.ExecuteScalar() is string value) newest = value;
            }

            return new SymbolIndexStatus(
                documents, symbols, declarations, usages, newest,
                Histogram(connection, "SELECT kind, COUNT(*) FROM si_symbol GROUP BY kind ORDER BY 2 DESC, 1"),
                Histogram(connection, "SELECT kind, COUNT(*) FROM si_usage GROUP BY kind ORDER BY 2 DESC, 1"));
        }
        catch (SqliteException)
        {
            return new SymbolIndexStatus(0, 0, 0, 0, null, [], []);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the index; the next build re-indexes everything.</summary>
    public void Clear()
    {
        var connection = Open();
        if (connection is null) return;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM si_symbol; DELETE FROM si_usage; DELETE FROM si_metacode; DELETE FROM si_document;";
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

    private IReadOnlyList<SymbolDeclaration> Declarations(
        string where, string name, bool partial, string? kind, int limit, string order)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {DeclarationColumns} FROM si_symbol
                WHERE ({where}) AND ($kind IS NULL OR kind = $kind) {order} LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$partial", partial ? 1 : 0);
            command.Parameters.AddWithValue("$pattern", "%" + Escape(name) + "%");
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Clamp(limit));
            return ReadDeclarations(command);
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

    private IReadOnlyList<SymbolUsage> Usages(string where, string key, string? kind, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT symbol_key, name, kind, container_key, path, start, length, line, col, doc_hash
                FROM si_usage {where} LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", Clamp(limit));

            var usages = new List<SymbolUsage>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                usages.Add(new SymbolUsage(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.GetInt32(8), reader.GetString(9)));
            }

            return usages;
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

    private IReadOnlyList<SymbolMetacode> Metacode(string where, string key, int limit)
    {
        var connection = Open();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT symbol_key, path, kind, name, line, text, doc_hash FROM si_metacode WHERE {where};";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$limit", Clamp(limit));

            var trees = new List<SymbolMetacode>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                trees.Add(new SymbolMetacode(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), Decompress((byte[])reader[5]), reader.GetString(6)));
            }

            return trees;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<SymbolDeclaration> ReadDeclarations(SqliteCommand command)
    {
        var declarations = new List<SymbolDeclaration>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            declarations.Add(new SymbolDeclaration(
                Key: reader.GetString(0),
                Name: reader.GetString(1),
                Display: reader.GetString(2),
                Signature: reader.GetString(3),
                Kind: reader.GetString(4),
                Accessibility: reader.GetString(5),
                Modifiers: reader.GetString(6),
                ContainerKey: reader.GetString(7),
                ContainerName: reader.GetString(8),
                Namespace: reader.GetString(9),
                IsStatic: reader.GetInt32(10) != 0,
                IsAbstract: reader.GetInt32(11) != 0,
                IsVirtual: reader.GetInt32(12) != 0,
                IsOverride: reader.GetInt32(13) != 0,
                IsExtension: reader.GetInt32(14) != 0,
                Arity: reader.GetInt32(15),
                Summary: reader.IsDBNull(16) ? null : reader.GetString(16),
                Project: reader.GetString(17),
                Path: reader.GetString(18),
                Start: reader.GetInt32(19),
                Length: reader.GetInt32(20),
                Line: reader.GetInt32(21),
                Column: reader.GetInt32(22),
                EndLine: reader.GetInt32(23),
                EndColumn: reader.GetInt32(24),
                DocumentHash: reader.GetString(25)));
        }

        return declarations;
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IReadOnlyList<SymbolKindCount> Histogram(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var counts = new List<SymbolKindCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) counts.Add(new SymbolKindCount(reader.GetString(0), reader.GetInt32(1)));
        return counts;
    }

    /// <summary>A LIKE pattern takes its own metacharacters: a query for <c>%</c> must not match everything.</summary>
    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

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

    private static int Clamp(int limit) => limit is < 1 ? 1 : limit > 5000 ? 5000 : limit;
}

/// <summary>One kind of use and how often it occurs.</summary>
public sealed record SymbolKindCount(string Kind, int Count);

/// <summary>A member that uses a symbol, and how.</summary>
public sealed record SymbolContainerCount(string ContainerKey, int Count, string Kinds);

/// <summary>A symbol used from inside one member, and how.</summary>
public sealed record SymbolTargetCount(string SymbolKey, string Name, int Count, string Kinds);

/// <summary>What the index holds, as one query answers it.</summary>
public sealed record SymbolIndexStatus(
    int Documents,
    int Symbols,
    int Declarations,
    int Usages,
    string? Newest,
    IReadOnlyList<SymbolKindCount> DeclarationKinds,
    IReadOnlyList<SymbolKindCount> UsageKinds);
