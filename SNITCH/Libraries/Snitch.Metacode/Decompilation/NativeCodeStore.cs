using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Snitch.Metacode.Analysis;
using Snitch.Metacode.Rendering;
using Snitch.Storage;
using Snitch.Storage.Schema;

namespace Snitch.Metacode.Decompilation;

/// <summary>One native binary whose decompilation the solution store keeps, keyed on the SHA-256 of the file.</summary>
public sealed record NativeArtifactRecord(
    long Id,
    string Sha256,
    string Binary,
    string Path,
    string Image,
    string Language,
    string Entry,
    string ImageBase,
    int Functions,
    long SizeBytes,
    string? Ghidra,
    string? Headless,
    DateTimeOffset CreatedAt,
    bool Stored,
    string? Database = null,
    string? Error = null)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// One recovered function: Ghidra's C, the canonical pseudocode the model made of it, and the complexity
/// both views agree on. The entry address is what makes it findable again in the binary.
/// </summary>
public sealed record NativeFunctionRecord(
    long Artifact,
    int Ordinal,
    string Name,
    string Entry,
    string Signature,
    int Size,
    string Decompiled,
    string Pseudocode,
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int Statements,
    int Branches);

/// <summary>One call edge of the recovered program: the caller and the callee by entry address.</summary>
public sealed record NativeCallRecord(long Artifact, string Caller, string Callee);

/// <summary>
/// The native half of the solution store: what Ghidra recovered, in tables of its own
/// (<c>native_artifact</c>, <c>native_function</c>, <c>native_call</c>) rather than mixed into the
/// metacode of the solution's own documents.
/// <para>
/// The identity is the binary's SHA-256, so a copied or re-built file is recognised for what it is and
/// the same image is not stored twice. Everything here is rebuildable: the binary on disk is the truth.
/// </para>
/// </summary>
public interface INativeCodeStore
{
    /// <summary>The artifact stored for that SHA-256, or null.</summary>
    NativeArtifactRecord? Find(string sha256);

    /// <summary>
    /// Stores a Ghidra run. With <paramref name="force"/> an artifact already stored under the same hash
    /// is replaced (its functions and call edges with it); without it the existing row is returned and
    /// nothing is written. <paramref name="document"/> saves translating the snapshot twice when the
    /// caller already has it.
    /// </summary>
    NativeArtifactRecord Save(GhidraAnalysis analysis, bool force = false, MetacodeDocument? document = null);

    /// <summary>Which binaries the store holds, newest first.</summary>
    IReadOnlyList<NativeArtifactRecord> Artifacts(int limit = 100);

    /// <summary>The functions of one artifact, in snapshot order, optionally filtered by name or address.</summary>
    IReadOnlyList<NativeFunctionRecord> Functions(long artifactId, string? filter = null, int limit = 500);

    /// <summary>One function by name or entry address (with or without <c>0x</c>).</summary>
    NativeFunctionRecord? Function(long artifactId, string nameOrAddress);

    /// <summary>The call edges of one artifact.</summary>
    IReadOnlyList<NativeCallRecord> Calls(long artifactId, int limit = 5000);

    int FunctionCount(long artifactId);
}

/// <summary>
/// SQLite implementation. It owns its connection to the same <c>.visualMCP/visualmcp.db</c> the other
/// stores use and degrades to a no-op answer when no solution is loaded or SQLite is unavailable: the
/// decompilation itself has already happened, and losing the copy must not lose the result.
/// </summary>
public sealed class NativeCodeStore : INativeCodeStore, IDisposable
{
    private const string ArtifactColumns = """
        id, sha256, binary, path, image, language, entry, image_base, functions, size_bytes,
        ghidra, headless, created_at
        """;

    private readonly IVisualMcpStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _path;
    private bool _unavailable;

    public NativeCodeStore(IVisualMcpStore store) => _store = store;

    /// <summary>The database the store is reading, or null when it is not open.</summary>
    public string? DatabasePath => _path;

    /// <summary>The SHA-256 of a file, as the artifact identity.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ── write ────────────────────────────────────────────────────────────────────────

    public NativeArtifactRecord Save(GhidraAnalysis analysis, bool force = false, MetacodeDocument? document = null)
    {
        var snapshot = analysis.Snapshot;
        var binary = Path.GetFileName(analysis.BinaryPath);

        if (snapshot is null)
            return Failed(analysis.BinaryPath, analysis.Error ?? "the Ghidra run produced no snapshot.");
        if (string.IsNullOrWhiteSpace(analysis.BinaryPath) || !File.Exists(analysis.BinaryPath))
            return Failed(analysis.BinaryPath, "the binary is no longer on disk.");

        string sha;
        long size;
        try
        {
            sha = HashOf(analysis.BinaryPath);
            size = new FileInfo(analysis.BinaryPath).Length;
        }
        catch (Exception exception)
        {
            return Failed(analysis.BinaryPath, "the binary could not be read: " + exception.Message);
        }

        var rows = BuildRows(snapshot, document ?? analysis.ToMetacode());
        var ghidraVersion = VersionOf(analysis.HeadlessPath);
        var database = _store.EnsureDatabase();
        if (database is null)
        {
            return new NativeArtifactRecord(
                0, sha, binary, analysis.BinaryPath, snapshot.Image, snapshot.Language, snapshot.Entry,
                snapshot.ImageBase, snapshot.Functions.Count, size, null, analysis.HeadlessPath,
                DateTimeOffset.UtcNow, Stored: false, Database: null,
                Error: "No solution is loaded, so there is nowhere to store the decompilation.");
        }

        var connection = Open(database);
        if (connection is null)
        {
            return new NativeArtifactRecord(
                0, sha, binary, analysis.BinaryPath, snapshot.Image, snapshot.Language, snapshot.Entry,
                snapshot.ImageBase, snapshot.Functions.Count, size, null, analysis.HeadlessPath,
                DateTimeOffset.UtcNow, Stored: false, Database: database,
                Error: "The solution database could not be opened (is the solution folder writable?).");
        }

        _gate.Wait();
        try
        {
            var existing = ReadArtifact(connection, sha);
            if (existing is not null && !force)
            {
                return existing with
                {
                    Binary = binary,
                    Path = analysis.BinaryPath,
                    SizeBytes = size,
                    Headless = analysis.HeadlessPath,
                    Database = database,
                    Stored = false,
                };
            }

            using var transaction = connection.BeginTransaction();

            long id;
            var now = DateTimeOffset.UtcNow.ToString("O");

            if (existing is not null)
            {
                id = existing.Id;
                using (var deleteFunctions = connection.CreateCommand())
                {
                    deleteFunctions.Transaction = transaction;
                    deleteFunctions.CommandText = "DELETE FROM native_function WHERE artifact = $artifact;";
                    deleteFunctions.Parameters.AddWithValue("$artifact", id);
                    deleteFunctions.ExecuteNonQuery();
                }

                using (var deleteCalls = connection.CreateCommand())
                {
                    deleteCalls.Transaction = transaction;
                    deleteCalls.CommandText = "DELETE FROM native_call WHERE artifact = $artifact;";
                    deleteCalls.Parameters.AddWithValue("$artifact", id);
                    deleteCalls.ExecuteNonQuery();
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE native_artifact SET
                            binary = $binary, path = $path, image = $image, language = $language,
                            entry = $entry, image_base = $imageBase, functions = $functions,
                            size_bytes = $size, ghidra = $ghidra, headless = $headless, created_at = $at
                        WHERE id = $id;
                        """;
                    BindArtifact(update, analysis, snapshot.Image, snapshot.Language, snapshot.Entry,
                        snapshot.ImageBase, snapshot.Functions.Count, size, sha, binary, ghidraVersion, now);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                }
            }
            else
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO native_artifact
                            (sha256, binary, path, image, language, entry, image_base, functions, size_bytes,
                             ghidra, headless, created_at)
                        VALUES ($sha, $binary, $path, $image, $language, $entry, $imageBase, $functions, $size,
                                $ghidra, $headless, $at);
                        """;
                    BindArtifact(insert, analysis, snapshot.Image, snapshot.Language, snapshot.Entry,
                        snapshot.ImageBase, snapshot.Functions.Count, size, sha, binary, ghidraVersion, now);
                    insert.ExecuteNonQuery();
                }

                using (var lastId = connection.CreateCommand())
                {
                    lastId.Transaction = transaction;
                    lastId.CommandText = "SELECT last_insert_rowid();";
                    id = Convert.ToInt64(lastId.ExecuteScalar());
                }
            }

            using (var function = connection.CreateCommand())
            {
                function.Transaction = transaction;
                function.CommandText = """
                    INSERT INTO native_function
                        (artifact, ordinal, name, entry, signature, size, decompiled, pseudocode,
                         cyclomatic, cognitive, max_nesting, statements, branches)
                    VALUES ($artifact, $ordinal, $name, $entry, $signature, $size, $decompiled, $pseudocode,
                            $cyclomatic, $cognitive, $nesting, $statements, $branches);
                    """;
                var artifact = function.Parameters.Add("$artifact", SqliteType.Integer);
                var ordinal = function.Parameters.Add("$ordinal", SqliteType.Integer);
                var name = function.Parameters.Add("$name", SqliteType.Text);
                var entry = function.Parameters.Add("$entry", SqliteType.Text);
                var signature = function.Parameters.Add("$signature", SqliteType.Text);
                var functionSize = function.Parameters.Add("$size", SqliteType.Integer);
                var decompiled = function.Parameters.Add("$decompiled", SqliteType.Text);
                var pseudocode = function.Parameters.Add("$pseudocode", SqliteType.Text);
                var cyclomatic = function.Parameters.Add("$cyclomatic", SqliteType.Integer);
                var cognitive = function.Parameters.Add("$cognitive", SqliteType.Integer);
                var nesting = function.Parameters.Add("$nesting", SqliteType.Integer);
                var statements = function.Parameters.Add("$statements", SqliteType.Integer);
                var branches = function.Parameters.Add("$branches", SqliteType.Integer);

                foreach (var row in rows.Functions)
                {
                    artifact.Value = id;
                    ordinal.Value = row.Ordinal;
                    name.Value = row.Name;
                    entry.Value = row.Entry;
                    signature.Value = row.Signature;
                    functionSize.Value = row.Size;
                    decompiled.Value = row.Decompiled;
                    pseudocode.Value = row.Pseudocode;
                    cyclomatic.Value = row.Cyclomatic;
                    cognitive.Value = row.Cognitive;
                    nesting.Value = row.MaxNesting;
                    statements.Value = row.Statements;
                    branches.Value = row.Branches;
                    function.ExecuteNonQuery();
                }
            }

            using (var call = connection.CreateCommand())
            {
                call.Transaction = transaction;
                call.CommandText = """
                    INSERT OR IGNORE INTO native_call (artifact, caller, callee)
                    VALUES ($artifact, $caller, $callee);
                    """;
                var artifact = call.Parameters.Add("$artifact", SqliteType.Integer);
                var caller = call.Parameters.Add("$caller", SqliteType.Text);
                var callee = call.Parameters.Add("$callee", SqliteType.Text);

                foreach (var edge in rows.Calls)
                {
                    artifact.Value = id;
                    caller.Value = edge.Caller;
                    callee.Value = edge.Callee;
                    call.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            return new NativeArtifactRecord(
                id, sha, binary, analysis.BinaryPath, snapshot.Image, snapshot.Language, snapshot.Entry,
                snapshot.ImageBase, snapshot.Functions.Count, size, ghidraVersion, analysis.HeadlessPath,
                DateTimeOffset.Parse(now), Stored: true, Database: database);
        }
        catch (SqliteException exception)
        {
            return Failed(analysis.BinaryPath, "the solution database refused the write: " + exception.Message, sha, database);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── read ─────────────────────────────────────────────────────────────────────────

    public NativeArtifactRecord? Find(string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        var connection = OpenStore();
        if (connection is null) return null;

        _gate.Wait();
        try
        {
            return ReadArtifact(connection, sha256.Trim().ToLowerInvariant());
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

    public IReadOnlyList<NativeArtifactRecord> Artifacts(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var connection = OpenStore();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {ArtifactColumns} FROM native_artifact ORDER BY created_at DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);

            var artifacts = new List<NativeArtifactRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) artifacts.Add(ReadArtifact(reader, stored: true));
            return artifacts;
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

    public IReadOnlyList<NativeFunctionRecord> Functions(long artifactId, string? filter = null, int limit = 500)
    {
        limit = Math.Clamp(limit, 1, 20_000);
        var connection = OpenStore();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(filter)
                ? """
                  SELECT artifact, ordinal, name, entry, signature, size, decompiled, pseudocode,
                         cyclomatic, cognitive, max_nesting, statements, branches
                  FROM native_function WHERE artifact = $artifact ORDER BY ordinal LIMIT $limit;
                  """
                : """
                  SELECT artifact, ordinal, name, entry, signature, size, decompiled, pseudocode,
                         cyclomatic, cognitive, max_nesting, statements, branches
                  FROM native_function
                  WHERE artifact = $artifact AND (name LIKE $filter OR entry LIKE $filter)
                  ORDER BY ordinal LIMIT $limit;
                  """;
            command.Parameters.AddWithValue("$artifact", artifactId);
            command.Parameters.AddWithValue("$limit", limit);
            if (!string.IsNullOrWhiteSpace(filter)) command.Parameters.AddWithValue("$filter", "%" + filter.Trim() + "%");

            var functions = new List<NativeFunctionRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) functions.Add(ReadFunction(reader));
            return functions;
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

    public NativeFunctionRecord? Function(long artifactId, string nameOrAddress)
    {
        if (string.IsNullOrWhiteSpace(nameOrAddress)) return null;
        var connection = OpenStore();
        if (connection is null) return null;

        var wanted = nameOrAddress.Trim();
        var address = wanted.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? wanted[2..] : wanted;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT artifact, ordinal, name, entry, signature, size, decompiled, pseudocode,
                       cyclomatic, cognitive, max_nesting, statements, branches
                FROM native_function
                WHERE artifact = $artifact AND (name = $name COLLATE NOCASE OR entry = $entry COLLATE NOCASE)
                ORDER BY ordinal LIMIT 1;
                """;
            command.Parameters.AddWithValue("$artifact", artifactId);
            command.Parameters.AddWithValue("$name", wanted);
            command.Parameters.AddWithValue("$entry", address);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadFunction(reader) : null;
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

    public IReadOnlyList<NativeCallRecord> Calls(long artifactId, int limit = 5000)
    {
        limit = Math.Clamp(limit, 1, 50_000);
        var connection = OpenStore();
        if (connection is null) return [];

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT artifact, caller, callee FROM native_call
                WHERE artifact = $artifact ORDER BY caller, callee LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$artifact", artifactId);
            command.Parameters.AddWithValue("$limit", limit);

            var edges = new List<NativeCallRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                edges.Add(new NativeCallRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
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

    public int FunctionCount(long artifactId)
    {
        var connection = OpenStore();
        if (connection is null) return 0;

        _gate.Wait();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM native_function WHERE artifact = $artifact;";
            command.Parameters.AddWithValue("$artifact", artifactId);
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
    }

    // ── rows ─────────────────────────────────────────────────────────────────────────

    private sealed record RowSet(
        IReadOnlyList<NativeFunctionRecord> Functions,
        IReadOnlyList<(string Caller, string Callee)> Calls);

    /// <summary>
    /// One pass over the model gives both the per-function pseudocode and the complexity, so a function
    /// that the C reader could not place costs that row (empty pseudocode, default score), not the binary.
    /// </summary>
    private static RowSet BuildRows(GhidraSnapshot snapshot, MetacodeDocument document)
    {
        var parsed = document.Modules
            .SelectMany(module => module.Declarations)
            .OfType<MetacodeFunction>()
            .GroupBy(function => function.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var renderer = new PseudocodeRenderer();
        var functions = new List<NativeFunctionRecord>(snapshot.Functions.Count);
        var calls = new HashSet<(string, string)>();

        for (var i = 0; i < snapshot.Functions.Count; i++)
        {
            var function = snapshot.Functions[i];
            parsed.TryGetValue(function.Name, out var model);

            var pseudocode = model is null ? string.Empty : renderer.Render(model).TrimEnd();
            var metrics = model is null ? MetacodeComplexityMetrics.Empty : MetacodeComplexityAnalyzer.Analyze(model);

            functions.Add(new NativeFunctionRecord(
                Artifact: 0,
                Ordinal: i,
                Name: function.Name,
                Entry: function.Entry,
                Signature: function.Signature,
                Size: function.Size,
                Decompiled: function.Decompiled,
                Pseudocode: pseudocode,
                Cyclomatic: metrics.Cyclomatic,
                Cognitive: metrics.Cognitive,
                MaxNesting: metrics.MaxNesting,
                Statements: metrics.Statements,
                Branches: metrics.Branches));

            foreach (var callee in function.Calls)
                calls.Add((function.Entry, callee));
        }

        return new RowSet(functions, calls.Select(edge => (edge.Item1, edge.Item2)).ToList());
    }

    private static NativeArtifactRecord Failed(string path, string error, string? sha = null, string? database = null) =>
        new(
            0, sha ?? string.Empty, Path.GetFileName(path), path, string.Empty, string.Empty, string.Empty,
            string.Empty, 0, 0, null, null, DateTimeOffset.UtcNow, Stored: false, Database: database, Error: error);

    private static void BindArtifact(
        SqliteCommand command,
        GhidraAnalysis analysis,
        string image,
        string language,
        string entry,
        string imageBase,
        int functions,
        long size,
        string sha,
        string binary,
        string? ghidraVersion,
        string at)
    {
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$binary", binary);
        command.Parameters.AddWithValue("$path", analysis.BinaryPath);
        command.Parameters.AddWithValue("$image", image);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$entry", entry);
        command.Parameters.AddWithValue("$imageBase", imageBase);
        command.Parameters.AddWithValue("$functions", functions);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$ghidra", (object?)ghidraVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$headless", (object?)analysis.HeadlessPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at);
    }

    /// <summary>The version the tools folder names: <c>.../ghidra_12.2_PUBLIC/support/analyzeHeadless</c>.</summary>
    private static string? VersionOf(string? headless)
    {
        if (string.IsNullOrWhiteSpace(headless)) return null;

        var home = Path.GetDirectoryName(Path.GetDirectoryName(headless));
        var folder = home is null ? null : Path.GetFileName(home);
        if (string.IsNullOrWhiteSpace(folder)) return null;

        const string prefix = "ghidra_";
        var start = folder.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        start += prefix.Length;
        var end = folder.IndexOf('_', start);
        return end > start ? folder[start..end] : folder[start..];
    }

    private static NativeArtifactRecord? ReadArtifact(SqliteConnection connection, string sha)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ArtifactColumns} FROM native_artifact WHERE sha256 = $sha;";
        command.Parameters.AddWithValue("$sha", sha);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadArtifact(reader, stored: true) : null;
    }

    private static NativeArtifactRecord ReadArtifact(SqliteDataReader reader, bool stored) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            DateTimeOffset.TryParse(reader.GetString(12), out var created) ? created : DateTimeOffset.MinValue,
            stored);

    private static NativeFunctionRecord ReadFunction(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12));

    // ── connection ───────────────────────────────────────────────────────────────────

    private SqliteConnection? OpenStore() => _store.EnsureDatabase() is { } database ? Open(database) : null;

    private SqliteConnection? Open(string database)
    {
        _gate.Wait();
        try
        {
            if (_connection is not null && string.Equals(_path, database, StringComparison.OrdinalIgnoreCase))
                return _connection;
            if (_unavailable) return null;

            try
            {
                _connection?.Dispose();
                _connection = null;

                Directory.CreateDirectory(Path.GetDirectoryName(database)!);

                var connection = new SqliteConnection($"Data Source={database}");
                connection.Open();

                using (var pragma = connection.CreateCommand())
                {
                    // The store, the board and the other indexes write this file too.
                    pragma.CommandText = "PRAGMA busy_timeout=5000;";
                    pragma.ExecuteNonQuery();
                }

                VisualMcpSchema.Ensure(connection);

                _connection = connection;
                _path = database;
                return connection;
            }
            catch (Exception)
            {
                _unavailable = true;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
