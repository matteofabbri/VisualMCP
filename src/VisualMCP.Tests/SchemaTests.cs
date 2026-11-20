using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Search;
using Snitch.Storage;
using Snitch.Storage.Schema;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The store is three SQLite databases under <c>.visualMCP/</c>, one per domain: <c>visualmcp.db</c>
/// (facts, logs, packages, text index), <c>board.db</c> (the plan) and <c>metacode.db</c> (the
/// indexes). These tests pin the tables each file must hold, prove a database written by an older
/// single-file build gains what it lacks without losing a row, and that the domains' tables really
/// move into their own file — verified, then dropped from the main one.
/// </summary>
public sealed class SchemaTests
{
    private static readonly string[] ExpectedMainTables =
    [
        "context", "command_log", "applied_edit", "file_snapshot", "package_fact", "package_dependency",
        "decompiled_artifact", "ti_document", "ti_row", "ti_line",
    ];

    private static readonly string[] ExpectedBoardTables =
    [
        "board_item", "board_comment", "board_history", "board_memory",
    ];

    private static readonly string[] ExpectedMetacodeTables =
    [
        "mc_document", "mc_tree", "mc_node", "source_document",
        "si_symbol", "si_usage", "si_document", "si_metacode",
        "native_artifact", "native_function", "native_call",
    ];

    [Fact]
    public void EachDomainModelCoversExactlyItsOwnTables()
    {
        Assert.Equal(
            ExpectedMainTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            VisualMcpSchema.Tables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            ExpectedBoardTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            VisualMcpSchema.BoardTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            ExpectedMetacodeTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            VisualMcpSchema.MetacodeTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFreshMainDatabaseGetsItsTables()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        var database = store.EnsureDatabase();

        Assert.NotNull(database);
        Assert.Empty(ExpectedMainTables.Except(Tables(database!)));
    }

    [Fact]
    public void AFreshWorkspacePreparesBothDomainDatabases()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        var board = store.EnsureBoardDatabase();
        var metacode = store.EnsureMetacodeDatabase();

        Assert.NotNull(board);
        Assert.NotNull(metacode);
        Assert.EndsWith("board.db", board, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("metacode.db", metacode, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ExpectedBoardTables.Except(Tables(board!)));
        Assert.Empty(ExpectedMetacodeTables.Except(Tables(metacode!)));
    }

    [Fact]
    public void AnOlderDatabaseKeepsItsRowsAndGainsWhatItLacks()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var storeDirectory = Path.Combine(workspace.RootDirectory, ".visualMCP");
        Directory.CreateDirectory(storeDirectory);
        var database = Path.Combine(storeDirectory, "visualmcp.db");

        // What a build from before the model wrote: one table, one row, and an applied_edit whose
        // before_text column did not exist yet.
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE context (
                    key        TEXT PRIMARY KEY,
                    value      TEXT NOT NULL,
                    stamp      TEXT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE applied_edit (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    applied_at   TEXT NOT NULL,
                    tool         TEXT NOT NULL,
                    rules        TEXT NULL,
                    file         TEXT NOT NULL,
                    before_hash  TEXT NOT NULL,
                    after_hash   TEXT NOT NULL,
                    before_chars INTEGER NOT NULL,
                    after_chars  INTEGER NOT NULL
                );
                INSERT INTO context VALUES ('kept', 'yes', NULL, '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        using var store = new VisualMcpStore(workspace);

        Assert.Equal("yes", store.Get("kept")!.Value);
        Assert.Empty(ExpectedMainTables.Except(Tables(database)));

        // The column the edit history gained after the table first shipped is still added in place.
        Assert.Contains("before_text", Columns(database, "applied_edit"));
    }

    [Fact]
    public void AnOlderBuildsBoardMigratesIntoItsOwnFile()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var storeDirectory = Path.Combine(workspace.RootDirectory, ".visualMCP");
        Directory.CreateDirectory(storeDirectory);
        var database = Path.Combine(storeDirectory, "visualmcp.db");

        // What the previous single-file build wrote: the board's tables beside the main ones, with
        // a card and a note in them.
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            VisualMcpSchema.Ensure(connection);
            VisualMcpSchema.EnsureBoard(connection);

            using var context = VisualMcpSchema.CreateBoardContext(connection);
            context.BoardItems.Add(new BoardItemRow
            {
                Title = "Kept card", Status = "todo", Priority = "medium", Position = 0,
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            });
            context.BoardMemories.Add(new BoardMemoryRow
            {
                Topic = "kept-note", Body = "the note survives the split", Kind = "fact",
                CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
                UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            });
            context.SaveChanges();
        }

        using var store = new VisualMcpStore(workspace);
        var board = store.EnsureBoardDatabase();

        Assert.NotNull(board);
        Assert.Equal("Kept card", Scalar(board!, "SELECT title FROM board_item LIMIT 1;"));
        Assert.Equal("the note survives the split", Scalar(board!, "SELECT body FROM board_memory LIMIT 1;"));

        // The rows moved, not copied: the main file holds no board table any more.
        Assert.False(HasTable(database, "board_item"));
        Assert.False(HasTable(database, "board_memory"));
    }

    [Fact]
    public void AnOlderBuildsIndexesMigrateIntoTheirOwnFile()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var storeDirectory = Path.Combine(workspace.RootDirectory, ".visualMCP");
        Directory.CreateDirectory(storeDirectory);
        var database = Path.Combine(storeDirectory, "visualmcp.db");

        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            VisualMcpSchema.Ensure(connection);
            VisualMcpSchema.EnsureMetacode(connection);

            using var context = VisualMcpSchema.CreateMetacodeContext(connection);
            context.MetacodeDocuments.Add(new MetacodeDocumentRow
            {
                Path = "D:\\old\\Counter.cs", Project = "Probe", Hash = "H", Mtime = 0,
                Language = "cs", Declarations = 1, Nodes = 3, Diagnostics = 0,
                IndexedAt = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"),
            });
            context.SaveChanges();
        }

        using var store = new VisualMcpStore(workspace);
        var metacode = store.EnsureMetacodeDatabase();

        Assert.NotNull(metacode);
        Assert.Equal("1", Scalar(metacode!, "SELECT COUNT(*) FROM mc_document;"));

        Assert.False(HasTable(database, "mc_document"));
        Assert.False(HasTable(database, "si_symbol"));
        Assert.False(HasTable(database, "native_artifact"));
    }

    [Fact]
    public void AColumnShuffleInALegacyTableKeepsTheValuesWithTheRightColumns()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var storeDirectory = Path.Combine(workspace.RootDirectory, ".visualMCP");
        Directory.CreateDirectory(storeDirectory);
        var database = Path.Combine(storeDirectory, "visualmcp.db");

        // A real older model held kind before name/line and trailed the newest columns; the row's
        // values must land in the columns they were written into, not in today's positions.
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            VisualMcpSchema.Ensure(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE board_item (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    title       TEXT NOT NULL,
                    status      TEXT NOT NULL,
                    priority    TEXT NOT NULL,
                    description TEXT NULL,
                    labels      TEXT NULL,
                    assignee    TEXT NULL,
                    position    INTEGER NOT NULL,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT NOT NULL
                );
                INSERT INTO board_item (title, status, priority, position, created_at, updated_at)
                VALUES ('Shuffled card', 'todo', 'high', 3, '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
                """;
            command.ExecuteNonQuery();
        }

        using var store = new VisualMcpStore(workspace);
        var board = store.EnsureBoardDatabase();

        Assert.NotNull(board);
        Assert.Equal("Shuffled card", Scalar(board!, "SELECT title FROM board_item LIMIT 1;"));
        Assert.Equal("todo", Scalar(board!, "SELECT status FROM board_item LIMIT 1;"));
        Assert.Equal("high", Scalar(board!, "SELECT priority FROM board_item LIMIT 1;"));
        Assert.Equal("3", Scalar(board!, "SELECT position FROM board_item LIMIT 1;"));
    }

    [Fact]
    public void RunningEnsureOnAnOpenDatabaseChangesNothing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        store.Set("key", "value");
        var database = store.EnsureDatabase()!;

        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            VisualMcpSchema.Ensure(connection);
            VisualMcpSchema.Ensure(connection);
        }

        Assert.Equal("value", store.Get("key")!.Value);
    }

    [Fact]
    public void TheGeneratedTablesKeepAutoincrementAndTheUniqueArtifactHash()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var database = store.EnsureDatabase()!;
        var board = store.EnsureBoardDatabase()!;
        var metacode = store.EnsureMetacodeDatabase()!;

        // A deleted card must not hand its id to the next one: board_history rows reference it.
        Assert.Contains("AUTOINCREMENT", Sql(board, "board_item"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUTOINCREMENT", Sql(database, "command_log"), StringComparison.OrdinalIgnoreCase);

        // The artifact identity is the SHA-256 of the binary, so the database refuses a second row.
        Assert.Contains(
            "UNIQUE",
            Sql(metacode, "native_artifact_sha256") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // Newest first: the log and the edit history are read by descending time.
        Assert.Contains("DESC", Sql(database, "command_log_at") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", Sql(database, "applied_edit_file") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachDomainModelDescribesTheSameColumnsAsItsDatabase()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var database = store.EnsureDatabase()!;
        var board = store.EnsureBoardDatabase()!;
        var metacode = store.EnsureMetacodeDatabase()!;

        foreach (var (file, context) in new (string, DbContext)[]
                 {
                     (database, VisualMcpSchema.CreateContext(new SqliteConnection($"Data Source={database}"))),
                     (board, VisualMcpSchema.CreateBoardContext(new SqliteConnection($"Data Source={board}"))),
                     (metacode, VisualMcpSchema.CreateMetacodeContext(new SqliteConnection($"Data Source={metacode}"))),
                 })
        using (context)
        {
            foreach (var entity in context.Model.GetEntityTypes())
            {
                var table = entity.GetTableName()!;
                var model = entity.GetProperties()
                    .Select(property => property.GetColumnName())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                Assert.Equal(
                    model.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                    Columns(file, table).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void TheFullTextTableIsCreatedAndSearchable()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var database = store.EnsureDatabase()!;

        using var index = new TextIndexStore();
        Assert.True(index.Open(database));

        // The indexed column splits identifiers, so a camel-cased name is found by one of its words.
        index.ReplaceAsync("Probe.cs", "source", "hash", 10, 1, ["var service = GetRequiredService();"])
            .GetAwaiter().GetResult();

        var (rows, error) = index.Search("required", null, 10);

        Assert.Null(error);
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Line);
    }

    [Fact]
    public void TheContextReadsWhatTheRawSqlWrites()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        store.Set("inspection:probe", "{\"value\":1}", "stamp");
        var database = store.EnsureDatabase()!;

        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var context = VisualMcpSchema.CreateContext(connection);

        var fact = context.ContextFacts.Single(row => row.Key == "inspection:probe");

        Assert.Equal("{\"value\":1}", fact.Value);
        Assert.Equal("stamp", fact.Stamp);
        Assert.True(fact.UpdatedAt > DateTimeOffset.UnixEpoch);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Tables(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static bool HasTable(string database, string table)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static IReadOnlyList<string> Columns(string database, string table)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        // The name comes from the model, not from a caller: PRAGMA does not take a parameter.
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>The DDL SQLite keeps for a table or index, or null when the name is not there.</summary>
    private static string? Sql(string database, string name)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE lower(name) = lower($name) LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static string Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
