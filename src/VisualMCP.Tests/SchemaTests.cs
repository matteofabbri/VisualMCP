using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Search;
using Snitch.Storage;
using Snitch.Storage.Schema;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The schema of <c>.visualMCP/visualmcp.db</c> is the EF Core model: these tests pin the tables it must
/// create, prove a database written by an older build gains what it lacks without losing a row, and that
/// the model and the raw SQL of the stores agree on the same file.
/// </summary>
public sealed class SchemaTests
{
    /// <summary>Every table the six stores read or write, including the FTS5 virtual table.</summary>
    private static readonly string[] ExpectedTables =
    [
        "context", "command_log", "applied_edit", "package_fact", "package_dependency", "decompiled_artifact",
        "board_item", "board_comment", "board_history",
        "ti_document", "ti_row", "ti_line",
        "mc_document", "mc_tree", "mc_node", "source_document",
        "si_symbol", "si_usage", "si_document", "si_metacode",
        "native_artifact", "native_function", "native_call",
    ];

    [Fact]
    public void TheModelCoversEveryTableTheStoresUse()
    {
        Assert.Equal(
            ExpectedTables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            VisualMcpSchema.Tables.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFreshDatabaseGetsEveryTable()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        var database = store.EnsureDatabase();

        Assert.NotNull(database);
        Assert.Empty(ExpectedTables.Except(Tables(database!)));
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
        Assert.Empty(ExpectedTables.Except(Tables(database)));

        // The column the edit history gained after the table first shipped is still added in place.
        Assert.Contains("before_text", Columns(database, "applied_edit"));
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

        // A deleted card must not hand its id to the next one: board_history rows reference it.
        Assert.Contains("AUTOINCREMENT", Sql(database, "board_item"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AUTOINCREMENT", Sql(database, "command_log"), StringComparison.OrdinalIgnoreCase);

        // The artifact identity is the SHA-256 of the binary, so the database refuses a second row.
        Assert.Contains(
            "UNIQUE",
            Sql(database, "native_artifact_sha256") ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        // Newest first: the log and the edit history are read by descending time.
        Assert.Contains("DESC", Sql(database, "command_log_at") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", Sql(database, "applied_edit_file") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheModelDescribesTheSameColumnsAsTheDatabase()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var database = store.EnsureDatabase()!;

        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var context = VisualMcpSchema.CreateContext(connection);

        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            var model = entity.GetProperties()
                .Select(property => property.GetColumnName())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(
                model.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                Columns(database, table).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
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
}
