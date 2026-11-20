using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Snitch.Storage;
using Snitch.Storage.Schema;

namespace VisualMCP.Dashboard;

/// <summary>One table or view found in the solution store.</summary>
public sealed record DashboardTable(string Name, string Kind, string? Definition)
{
    /// <summary>An FTS5 table: browsable, but its shadow tables are not.</summary>
    public bool IsVirtual =>
        Definition is not null && Definition.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One column as SQLite reports it through <c>PRAGMA table_info</c>.</summary>
public sealed record DashboardColumn(string Name, string Type, bool NotNull, bool PrimaryKey)
{
    public bool IsBlob => Type.Contains("BLOB", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A table with its row count and columns, for the catalogue and schema pages.</summary>
public sealed record DashboardTableInfo(DashboardTable Table, long Rows, IReadOnlyList<DashboardColumn> Columns);

/// <summary>One invoked command as the left column lists it: which tool, and when it arrived.</summary>
public sealed record DashboardCommand(string Tool, string OccurredAt);

/// <summary>The commands invoked so far, newest first, plus the total in the log.</summary>
public sealed record DashboardCommandTrail(long Total, IReadOnlyList<DashboardCommand> Items);

/// <summary>One page of rows of a table, with the query that produced them.</summary>
public sealed record DashboardRows(
    DashboardTable Table,
    IReadOnlyList<DashboardColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    long Total,
    int Page,
    int PageSize,
    string? Sort,
    bool Descending,
    string? Search)
{
    public int PageCount => (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Read-only access to the solution store for the dashboard. The store is split per domain —
/// <c>visualmcp.db</c>, <c>board.db</c> and <c>metacode.db</c> under <c>&lt;solution&gt;/.visualMCP/</c> —
/// and every query is opened against the database that owns its table, so the dashboard reads the
/// same files the services write. It opens its own connection per query, so it never competes with
/// the store, the board or the indexes for a long-lived handle, and every table name it executes
/// comes from <c>sqlite_master</c> rather than from the request.
/// </summary>
public sealed class DashboardDatabase
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 1000;

    /// <summary>Rows a CSV export may contain; the page is not the limit, the file is.</summary>
    public const int ExportLimit = 20_000;

    private static readonly TimeSpan CountLifetime = TimeSpan.FromSeconds(3);

    private readonly IVisualMcpStore _store;
    private readonly ConcurrentDictionary<string, (long Rows, DateTimeOffset At)> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _hasRowId = new(StringComparer.Ordinal);

    public DashboardDatabase(IVisualMcpStore store) => _store = store;

    /// <summary>
    /// The database file, or null when no solution has been located. Asking the store also
    /// creates the file and its schema on first use, so an empty dashboard becomes the real
    /// one as soon as the environment is known.
    /// </summary>
    public string? DatabasePath
    {
        get
        {
            try { return _store.DatabasePath ?? _store.EnsureDatabase(); }
            catch { return _store.DatabasePath; }
        }
    }

    public bool Exists
    {
        get
        {
            var path = DatabasePath;
            return path is not null && File.Exists(path);
        }
    }

    /// <summary>
    /// The bytes of every store file that exists right now — the main store plus whichever domain
    /// databases have been prepared. With the store split per domain this, not the main file alone,
    /// is what the store costs on disk.
    /// </summary>
    public long SizeBytes
    {
        get
        {
            try
            {
                long total = 0;
                foreach (var path in DomainFiles())
                {
                    total += new FileInfo(path).Length;
                    var wal = path + "-wal";
                    if (File.Exists(wal)) total += new FileInfo(wal).Length;
                }

                return total;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    /// <summary>Every browsable table and view across the store's databases, shadow tables excluded.</summary>
    public IReadOnlyList<DashboardTable> Tables()
    {
        var tables = new List<DashboardTable>();
        foreach (var path in DomainFiles())
        {
            using var connection = OpenAt(path);
            if (connection is not null) tables.AddRange(ReadTables(connection));
        }

        return tables.OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One table with its row count and columns, or null when it does not exist.</summary>
    public DashboardTableInfo? Info(string? tableName)
    {
        var path = PathOf(tableName);
        using var connection = OpenAt(path);
        if (connection is null) return null;

        var table = FindTable(connection, tableName);
        if (table is null) return null;

        return new DashboardTableInfo(table, Count(connection, path ?? string.Empty, table.Name), Columns(connection, table.Name));
    }

    /// <summary>
    /// One page of a table: a substring filter over every non-blob column, an optional sort on
    /// one real column, and paging. Null when the table does not exist.
    /// </summary>
    public DashboardRows? Read(
        string? tableName, int page, int pageSize, string? sort, bool descending, string? search)
    {
        var path = PathOf(tableName);
        using var connection = OpenAt(path);
        if (connection is null) return null;

        var table = FindTable(connection, tableName);
        if (table is null) return null;

        var columns = Columns(connection, table.Name);
        if (columns.Count == 0)
            return new DashboardRows(table, columns, [], 0, 1, DefaultPageSize, null, descending, search);

        var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var filter = BuildFilter(columns, search, out var pattern);
        var total = Scalar(connection, $"SELECT COUNT(*) FROM {Quote(table.Name)} {filter};", pattern);

        var pages = (int)Math.Max(1, (total + size - 1) / size);
        var current = Math.Clamp(page, 1, pages);

        var sortColumn = columns.FirstOrDefault(c => string.Equals(c.Name, sort, StringComparison.OrdinalIgnoreCase))?.Name;
        var orderBy = sortColumn is not null
            ? $"ORDER BY {Quote(sortColumn)} {(descending ? "DESC" : "ASC")}"
            : HasRowId(connection, path ?? string.Empty, table.Name)
                ? "ORDER BY rowid DESC"      // newest row first: an id column is the rowid of these tables
                : $"ORDER BY {Quote(columns[0].Name)} ASC";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT * FROM {Quote(table.Name)} {filter} {orderBy} LIMIT $size OFFSET $offset;
            """;
        if (pattern is not null) command.Parameters.AddWithValue("$q", pattern);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$offset", (current - 1) * size);

        var rows = new List<object?[]>(size);
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }
        }
        catch (SqliteException)
        {
            return null;
        }

        return new DashboardRows(table, columns, rows, total, current, size, sortColumn, descending, search);
    }

    /// <summary>The filtered rows of a table for a CSV export, capped at <see cref="ExportLimit"/>.</summary>
    public IReadOnlyList<object?[]> Export(string? tableName, string? search, int limit = ExportLimit)
    {
        var path = PathOf(tableName);
        using var connection = OpenAt(path);
        if (connection is null) return [];

        var table = FindTable(connection, tableName);
        if (table is null) return [];

        var columns = Columns(connection, table.Name);
        var filter = BuildFilter(columns, search, out var pattern);
        var cap = Math.Clamp(limit, 1, ExportLimit);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT * FROM {Quote(table.Name)} {filter}
            {(HasRowId(connection, path ?? string.Empty, table.Name) ? "ORDER BY rowid DESC" : "")} LIMIT $limit;
            """;
        if (pattern is not null) command.Parameters.AddWithValue("$q", pattern);
        command.Parameters.AddWithValue("$limit", cap);

        var rows = new List<object?[]>();
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }
        }
        catch (SqliteException)
        {
            return [];
        }

        return rows;
    }

    /// <summary>Every table with its row count and columns, across all of the store's databases.</summary>
    public IReadOnlyList<DashboardTableInfo> Catalogue()
    {
        var infos = new List<DashboardTableInfo>();
        foreach (var path in DomainFiles())
        {
            using var connection = OpenAt(path);
            if (connection is null) continue;

            foreach (var table in ReadTables(connection))
                infos.Add(new DashboardTableInfo(table, Count(connection, path, table.Name), Columns(connection, table.Name)));
        }

        return infos.OrderBy(info => info.Table.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The most recently invoked commands, newest first, for the left column's list, together
    /// with the total in the log. A missing store or a log with no row is an answer, not an
    /// error: the sidebar says which of the two it is.
    /// </summary>
    public DashboardCommandTrail RecentCommands(int limit = 14)
    {
        var mainPath = DatabasePath;
        using var connection = OpenAt(mainPath);
        if (connection is null) return new DashboardCommandTrail(0, []);

        var table = FindTable(connection, "command_log");
        if (table is null) return new DashboardCommandTrail(0, []);

        var cap = Math.Clamp(limit, 1, 200);
        var commands = new List<DashboardCommand>(cap);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT tool, occurred_at FROM {Quote(table.Name)} ORDER BY id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", cap);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                commands.Add(new DashboardCommand(reader.GetString(0), reader.GetString(1)));
        }
        catch (SqliteException)
        {
            return new DashboardCommandTrail(0, []);
        }

        return new DashboardCommandTrail(Count(connection, mainPath ?? string.Empty, table.Name), commands);
    }

    /// <summary>
    /// The ports remembered by dashboard instances on this workspace, newest first. Each instance
    /// owns one <c>dashboard.port.&lt;pid&gt;</c> row, so two live servers no longer overwrite each
    /// other; the caller reuses only the entries whose process is gone.
    /// </summary>
    public IReadOnlyList<DashboardPort> StoredPorts()
    {
        var path = DatabasePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];

        try
        {
            using var connection = Open();
            if (connection is null) return [];

            var ports = new List<DashboardPort>();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"value\" FROM context WHERE \"key\" LIKE @prefix ORDER BY \"updated_at\" DESC;";
            command.Parameters.AddWithValue("@prefix", DashboardOptions.PortKeyPrefix + "%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (DashboardPort.Parse(reader.GetString(0)) is { } entry) ports.Add(entry);
            }

            return ports;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>Remembers this instance's port, so a later start of a dashboard can reuse it.</summary>
    public void RememberPort(int port)
    {
        var key = DashboardOptions.PortKeyPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        _store.Set(key, DashboardPort.Mine(port).Serialize());
    }

    // ── SQLite plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// The database file that owns a table: the board's tables live in <c>board.db</c> and the
    /// indexes' in <c>metacode.db</c>, everything else (including the FTS shadow tables) in the
    /// main store. The domain names come from the schema itself, so a table that moves domain
    /// moves its dashboard queries with it. Null when that domain has no database yet.
    /// </summary>
    private string? PathOf(string? tableName)
    {
        if (tableName is null) return DatabasePath;

        foreach (var table in VisualMcpSchema.BoardTables)
            if (string.Equals(table, tableName, StringComparison.OrdinalIgnoreCase))
                return BoardPath();

        foreach (var table in VisualMcpSchema.MetacodeTables)
            if (string.Equals(table, tableName, StringComparison.OrdinalIgnoreCase))
                return MetacodePath();

        return DatabasePath;
    }

    /// <summary>Every store file that exists on disk right now, main first, no duplicates.</summary>
    private List<string> DomainFiles()
    {
        var paths = new List<string>(3);

        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path)
                && File.Exists(path)
                && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        Add(DatabasePath);
        Add(BoardPath());
        Add(MetacodePath());
        return paths;
    }

    private string? BoardPath()
    {
        try { return _store.EnsureBoardDatabase(); }
        catch { return null; }
    }

    private string? MetacodePath()
    {
        try { return _store.EnsureMetacodeDatabase(); }
        catch { return null; }
    }

    /// <summary>The main store's connection, for its own tables and the remembered ports.</summary>
    private SqliteConnection? Open() => OpenAt(DatabasePath);

    /// <summary>The connection of the database that owns a table, or null when that file is missing.</summary>
    private SqliteConnection? Open(string? tableName) => OpenAt(PathOf(tableName));

    private SqliteConnection? OpenAt(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            // ReadWrite (not ReadOnly): an existing hot journal is rolled back on open, which a
            // read-only handle cannot do. The dashboard issues nothing but SELECTs.
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
            };

            var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 4000;";
            pragma.ExecuteNonQuery();

            return connection;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DashboardTable> ReadTables(SqliteConnection connection)
    {
        var tables = new List<DashboardTable>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, type, sql FROM sqlite_master
                WHERE type IN ('table', 'view')
                ORDER BY name;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
                tables.Add(new DashboardTable(name, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        catch (SqliteException)
        {
            return [];
        }

        // An FTS5 table owns shadow tables (…_data, …_idx, …_content, …_docsize, …_config):
        // they are the index's own storage, not something to browse.
        var virtualTables = tables.Where(t => t.IsVirtual).Select(t => t.Name).ToList();
        return virtualTables.Count == 0
            ? tables
            : tables.Where(t => !virtualTables.Any(v =>
                t.Name.StartsWith(v + "_", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>
    /// Resolves a requested name against <c>sqlite_master</c>. The name that reaches the SQL
    /// afterwards is the one the database returned, not the one the request supplied.
    /// </summary>
    private static DashboardTable? FindTable(SqliteConnection connection, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, type, sql FROM sqlite_master
                WHERE type IN ('table', 'view') AND lower(name) = lower($name) LIMIT 1;
                """;
            command.Parameters.AddWithValue("$name", name.Trim());

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var actual = reader.GetString(0);
            if (actual.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) return null;
            return new DashboardTable(actual, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static IReadOnlyList<DashboardColumn> Columns(SqliteConnection connection, string table)
    {
        var columns = new List<DashboardColumn>();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({Quote(table)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new DashboardColumn(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture) != 0,
                    Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture) != 0));
            }
        }
        catch (SqliteException)
        {
            return [];
        }

        return columns;
    }

    /// <summary>The row count, memoized for a few seconds so the overview is not a COUNT scan storm.
    /// The scope is the owning database's path, since a table name names one row of only one file.</summary>
    private long Count(SqliteConnection connection, string scope, string table)
    {
        var key = scope + "|" + table;
        if (_counts.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CountLifetime)
            return cached.Rows;

        var rows = Scalar(connection, $"SELECT COUNT(*) FROM {Quote(table)};", null);
        _counts[key] = (rows, DateTimeOffset.UtcNow);
        return rows;
    }

    /// <summary>SQLite's implicit <c>rowid</c>, which every non-WITHOUT-ROWID table has.</summary>
    private bool HasRowId(SqliteConnection connection, string scope, string table)
    {
        var key = scope + "|" + table;
        if (_hasRowId.TryGetValue(key, out var known)) return known;

        var supported = true;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rowid FROM {Quote(table)} LIMIT 0;";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            supported = false;
        }

        _hasRowId[key] = supported;
        return supported;
    }

    private static long Scalar(SqliteConnection connection, string sql, string? pattern)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (pattern is not null) command.Parameters.AddWithValue("$q", pattern);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// A LIKE over every column that is not a blob. The pattern is a parameter and the user's
    /// own wildcards are escaped, so a search for <c>50%</c> is a search for "50%".
    /// </summary>
    private static string BuildFilter(IReadOnlyList<DashboardColumn> columns, string? search, out string? pattern)
    {
        pattern = null;
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var usable = columns.Where(c => !c.IsBlob).ToList();
        if (usable.Count == 0) return string.Empty;

        pattern = "%" + text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

        var clauses = usable.Select(c => $"CAST({Quote(c.Name)} AS TEXT) LIKE $q ESCAPE '\\'");
        return "WHERE (" + string.Join(" OR ", clauses) + ")";
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
