using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Snitch.Storage.Schema;
using Snitch.Workspace;

namespace Snitch.Storage;

/// <summary>One stored context fact.</summary>
public sealed record ContextEntry(string Value, string? Stamp, DateTimeOffset UpdatedAt);

/// <summary>One write this server performed on a file, as recorded in the edit history.</summary>
public sealed record AppliedEditEntry(
    long Id, DateTimeOffset AppliedAt, string Tool, string? Rules, string File,
    string BeforeHash, string AfterHash, int BeforeChars, int AfterChars)
{
    /// <summary>False when the write left the text identical — a rewrite that reported but changed nothing.</summary>
    public bool ContentChanged => BeforeHash != AfterHash;

    /// <summary>Whether the pre-write text was kept, so the edit can be undone.</summary>
    public bool Revertible { get; init; }
}

/// <summary>A resolved NuGet package as the project actually uses it, one row per target framework.</summary>
public sealed record PackageFact(string Project, string Id, string Version, string Tfm, bool Direct);

/// <summary>One edge of the resolved package graph: who pulls what in, per target framework.</summary>
public sealed record PackageDependency(
    string Project, string ParentId, string ParentVersion, string Tfm, string ChildId, string ChildVersion);

/// <summary>
/// A decompiled assembly kept on disk, keyed on the module version id: the same binary always maps to
/// the same directory, so a second call reuses the source instead of running ILSpy again.
/// </summary>
public sealed record DecompiledArtifact(
    string Mvid, string AssemblyPath, string Name, string Directory, int Types, DateTimeOffset CreatedAt);

/// <summary>
/// The project's on-disk context store: a SQLite database at
/// <c>&lt;solution directory&gt;/.visualMCP/visualmcp.db</c>. It keeps facts that are expensive to
/// recompute (today: the last inspection report) so they survive a restart of the server.
/// </summary>
public interface IVisualMcpStore
{
    /// <summary>The database path, or null until a solution is known.</summary>
    string? DatabasePath { get; }

    /// <summary>
    /// Opens the store if it is not open yet and returns the database path, or null when it is
    /// unavailable. Other owners of the same file (the kanban board) open their own connection.
    /// </summary>
    string? EnsureDatabase();

    /// <summary>Stores a fact; <paramref name="stamp"/> is an opaque freshness token the caller compares.</summary>
    void Set(string key, string value, string? stamp = null);

    /// <summary>Reads a fact, or null when absent (or when the store is unavailable).</summary>
    ContextEntry? Get(string key);

    bool Remove(string key);

    /// <summary>
    /// Appends one received tool call (date, time, tool, arguments) to the command log. Silently does
    /// nothing when no database is available: logging must never fail a tool call.
    /// </summary>
    void RecordCommand(string tool, string? arguments = null);

    /// <summary>The most recent commands, newest first.</summary>
    IReadOnlyList<CommandLogEntry> RecentCommands(int limit, string? tool = null);

    /// <summary>How many commands are in the log.</summary>
    int CommandCount();

    /// <summary>
    /// Records one file write (who, which rules, before/after hashes). This is the solution's local
    /// history: it makes an applied rewrite auditable and revertible without keeping whole file copies.
    /// </summary>
    void RecordEdit(string tool, string file, string? rules, string beforeText, string afterText);

    /// <summary>The most recent edits, newest first, optionally for one file.</summary>
    IReadOnlyList<AppliedEditEntry> RecentEdits(int limit, string? file = null);

    /// <summary>How many edits are recorded.</summary>
    int EditCount();

    /// <summary>The recorded text of an edit before it happened, or null when it was not kept.</summary>
    string? EditBeforeText(long id);

    /// <summary>One edit by id, or null.</summary>
    AppliedEditEntry? Edit(long id);

    // ── package facts ────────────────────────────────────────────────────────
    /// <summary>Replaces a project's package facts (one row per package and target framework).</summary>
    void ReplacePackages(string project, IReadOnlyList<PackageFact> packages);

    /// <summary>Replaces a project's package dependency edges.</summary>
    void ReplacePackageDependencies(string project, IReadOnlyList<PackageDependency> dependencies);

    IReadOnlyList<PackageFact> Packages(string? project = null, int limit = 2000);

    /// <summary>The edges that pull a package in.</summary>
    IReadOnlyList<PackageDependency> PackageDependencies(string? project, string? childId = null, int limit = 500);

    int PackageCount();

    // ── decompiled artifacts ─────────────────────────────────────────────────
    /// <summary>Records where the source of an assembly was written, keyed on its MVID.</summary>
    void RecordArtifact(DecompiledArtifact artifact);

    /// <summary>The artifact for that module version id, or null.</summary>
    DecompiledArtifact? FindArtifact(string mvid);

    IReadOnlyList<DecompiledArtifact> Artifacts(int limit = 200);
}

/// <summary>
/// Lazily opens one database per solution directory. Every operation degrades to a no-op when no
/// solution is loaded or SQLite cannot be used: the store is an optimisation, never a dependency.
/// </summary>
public sealed partial class VisualMcpStore : IVisualMcpStore, IDisposable
{
    private readonly IRoslynWorkspace _workspace;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private string? _connectedDirectory;
    private bool _unavailable;

    public VisualMcpStore(IRoslynWorkspace workspace) => _workspace = workspace;

    public string? DatabasePath { get; private set; }

    public string? EnsureDatabase()
    {
        Connection();
        return DatabasePath;
    }

    public void Set(string key, string value, string? stamp = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var connection = Connection();
        if (connection is null) return;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO context (key, value, stamp, updated_at)
                VALUES ($key, $value, $stamp, $updated)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    stamp = excluded.stamp,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$stamp", (object?)stamp ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Degrade silently: a cache write failing must never fail the tool call.
        }
    }

    public ContextEntry? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var connection = Connection();
        if (connection is null) return null;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value, stamp, updated_at FROM context WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var updated = DateTimeOffset.TryParse(reader.GetString(2), out var parsed) ? parsed : DateTimeOffset.MinValue;
            return new ContextEntry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), updated);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public bool Remove(string key)
    {
        var connection = Connection();
        if (connection is null) return false;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM context WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteNonQuery() > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // ── command log ──────────────────────────────────────────────────────────
    public void RecordCommand(string tool, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(tool)) return;
        var connection = Connection();
        if (connection is null) return;

        var now = DateTimeOffset.UtcNow;
        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO command_log (occurred_at, date, time, tool, arguments)
                    VALUES ($at, $date, $time, $tool, $arguments);
                    """;
                command.Parameters.AddWithValue("$at", now.ToString("O"));
                command.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("$time", now.ToString("HH:mm:ss"));
                command.Parameters.AddWithValue("$tool", tool);
                command.Parameters.AddWithValue("$arguments", (object?)arguments ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
        {
            // A log write must never fail the tool call it describes.
        }
    }

    public IReadOnlyList<CommandLogEntry> RecentCommands(int limit, string? tool = null)
    {
        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;

        var connection = Connection();
        if (connection is null) return [];

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = tool is null
                    ? "SELECT id, occurred_at, tool, arguments FROM command_log ORDER BY id DESC LIMIT $limit;"
                    : "SELECT id, occurred_at, tool, arguments FROM command_log WHERE tool = $tool ORDER BY id DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit);
                if (tool is not null) command.Parameters.AddWithValue("$tool", tool);

                var entries = new List<CommandLogEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var at = DateTimeOffset.TryParse(reader.GetString(1), out var parsed) ? parsed : DateTimeOffset.MinValue;
                    entries.Add(new CommandLogEntry(
                        reader.GetInt64(0),
                        at,
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }

                return entries;
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    public int CommandCount()
    {
        var connection = Connection();
        if (connection is null) return 0;

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM command_log;";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    // ── edit history ─────────────────────────────────────────────────────────
    public void RecordEdit(string tool, string file, string? rules, string beforeText, string afterText)
    {
        if (string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(file)) return;
        var connection = Connection();
        if (connection is null) return;

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO applied_edit
                        (applied_at, tool, rules, file, before_hash, after_hash, before_chars, after_chars, before_text)
                    VALUES ($at, $tool, $rules, $file, $before, $after, $beforeChars, $afterChars, $beforeText);
                    """;
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$tool", tool);
                command.Parameters.AddWithValue("$rules", (object?)rules ?? DBNull.Value);
                command.Parameters.AddWithValue("$file", file);
                command.Parameters.AddWithValue("$before", ContentHash(beforeText));
                command.Parameters.AddWithValue("$after", ContentHash(afterText));
                command.Parameters.AddWithValue("$beforeChars", beforeText.Length);
                command.Parameters.AddWithValue("$afterChars", afterText.Length);
                command.Parameters.AddWithValue("$beforeText", Compress(beforeText));
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
        {
            // History is a convenience: losing a row must never fail the write it describes.
        }
    }

    public IReadOnlyList<AppliedEditEntry> RecentEdits(int limit, string? file = null)
    {
        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;

        var connection = Connection();
        if (connection is null) return [];

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = file is null
                    ? """
                      SELECT id, applied_at, tool, rules, file, before_hash, after_hash, before_chars, after_chars,
                             before_text IS NOT NULL
                      FROM applied_edit ORDER BY id DESC LIMIT $limit;
                      """
                    : """
                      SELECT id, applied_at, tool, rules, file, before_hash, after_hash, before_chars, after_chars,
                             before_text IS NOT NULL
                      FROM applied_edit WHERE file = $file ORDER BY id DESC LIMIT $limit;
                      """;
                command.Parameters.AddWithValue("$limit", limit);
                if (file is not null) command.Parameters.AddWithValue("$file", file);

                var entries = new List<AppliedEditEntry>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var at = DateTimeOffset.TryParse(reader.GetString(1), out var parsed) ? parsed : DateTimeOffset.MinValue;
                    entries.Add(new AppliedEditEntry(
                        reader.GetInt64(0), at, reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                        reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8))
                    {
                        Revertible = reader.GetInt64(9) != 0,
                    });
                }

                return entries;
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    public int EditCount()
    {
        var connection = Connection();
        if (connection is null) return 0;

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM applied_edit;";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A short content hash: enough to tell "the file changed" from "the rewrite reported itself but
    /// left the text identical", without keeping a copy of the text.
    /// </summary>
    public static string ContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    public AppliedEditEntry? Edit(long id)
    {
        var connection = Connection();
        if (connection is null) return null;

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, applied_at, tool, rules, file, before_hash, after_hash, before_chars, after_chars,
                           before_text IS NOT NULL
                    FROM applied_edit WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);

                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;

                var at = DateTimeOffset.TryParse(reader.GetString(1), out var parsed) ? parsed : DateTimeOffset.MinValue;
                return new AppliedEditEntry(
                    reader.GetInt64(0), at, reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8))
                {
                    Revertible = reader.GetInt64(9) != 0,
                };
            }
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public string? EditBeforeText(long id)
    {
        var connection = Connection();
        if (connection is null) return null;

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT before_text FROM applied_edit WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0)) return null;

                return Decompress((byte[])reader[0]);
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pre-write text is kept compressed so an edit can actually be undone: a hash alone proves
    /// that something changed, it cannot restore it.
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

    /// <summary>Adds a column to an existing table when it is missing; the schema itself is create-only.</summary>
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        try
        {
            using var inspect = connection.CreateCommand();
            inspect.CommandText = $"PRAGMA table_info({table});";
            var present = false;
            using (var reader = inspect.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }
            }

            if (present) return;

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // An older database that cannot be migrated simply keeps working without the column.
        }
    }

    private SqliteConnection? Connection()
    {
        lock (_gate)
        {
            if (_unavailable) return null;

            var solutionPath = _workspace.LoadedSolutionPath ?? _workspace.DiscoverSolutionPath();
            if (string.IsNullOrWhiteSpace(solutionPath)) return null;

            var solutionDirectory = Path.GetDirectoryName(solutionPath);
            if (string.IsNullOrWhiteSpace(solutionDirectory)) return null;

            // The host can switch the active solution; reopen when the directory changes.
            if (_connection is not null && string.Equals(_connectedDirectory, solutionDirectory, StringComparison.OrdinalIgnoreCase))
                return _connection;

            try
            {
                _connection?.Dispose();
                _connection = null;

                var storeDirectory = Path.Combine(solutionDirectory, ".visualMCP");
                Directory.CreateDirectory(storeDirectory);
                var databasePath = Path.Combine(storeDirectory, "visualmcp.db");

                var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA busy_timeout=5000;";
                    command.ExecuteNonQuery();
                }

                VisualMcpSchema.Ensure(connection);

                // A database created before the history kept file contents gains the column in place.
                EnsureColumn(connection, "applied_edit", "before_text", "BLOB");

                _connection = connection;
                _connectedDirectory = solutionDirectory;
                DatabasePath = databasePath;
                return connection;
            }
            catch (Exception)
            {
                // No SQLite, no write permission, read-only folder: run without the store.
                _unavailable = true;
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
