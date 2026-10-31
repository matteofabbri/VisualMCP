using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Snitch.Storage.Schema;

/// <summary>
/// Creates the tables of <c>&lt;solution&gt;/.visualMCP/visualmcp.db</c> from the EF Core model: the
/// schema is declared once in <see cref="VisualMcpDbContext"/> and this class turns it into DDL.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ensure"/> is safe on a fresh database and on one written by an older build: it reads what
/// <c>sqlite_master</c> already holds and executes only the statements whose table or index is missing.
/// It never drops a table or a row, so a store that runs without it keeps working — and several stores
/// open the same file with their own connection, so each of them can call it.
/// </para>
/// <para>
/// A COLUMN added to the model after a store was written is added in place (<c>ALTER TABLE … ADD COLUMN</c>)
/// with the column's own default, which is what lets the store gain a field — the metacode provenance
/// columns were the first — without a migration pass or a rebuild. A nullable column is added as NULL;
/// a required one as NOT NULL with the type's zero value, so existing rows read as "unknown" instead of
/// breaking the query. Nothing is ever dropped: a column the model no longer declares keeps its data.
/// </para>
/// <para>
/// The full-text table is the one exception to the model: <c>ti_line</c> is an FTS5 virtual table, which a
/// relational model cannot describe, so its DDL is kept here in SQL.
/// </para>
/// </remarks>
public static class VisualMcpSchema
{
    /// <summary>The FTS5 table the model cannot express: a virtual table, not a relational one.</summary>
    private const string FullText = """
        CREATE VIRTUAL TABLE IF NOT EXISTS ti_line USING fts5(
            path  UNINDEXED,
            line  UNINDEXED,
            text  UNINDEXED,
            words,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    private static readonly Lazy<(string[] Statements, string[] Tables, SchemaColumn[] Columns)> Script = new(Build);

    /// <summary>
    /// Every table <see cref="Ensure"/> guarantees, in script order, including the FTS5 virtual table.
    /// Tests read this to prove the model still covers what the stores query.
    /// </summary>
    public static IReadOnlyList<string> Tables => Script.Value.Tables;

    /// <summary>
    /// Creates whatever the model declares and the database does not hold yet — a missing table, and a
    /// column a newer model declares on a table an older store already had.
    /// </summary>
    public static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var (statements, _, columns) = Script.Value;
        var existing = ExistingObjects(connection);

        foreach (var statement in statements)
        {
            var name = CreatedObject(statement);
            if (name is not null && existing.Contains(name)) continue;

            using var command = connection.CreateCommand();
            command.CommandText = statement;
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Another process created it between the read and the statement. The table is there,
                // which is all this method promises.
            }

            if (name is not null) existing.Add(name);
        }

        using var fullText = connection.CreateCommand();
        fullText.CommandText = FullText;
        fullText.ExecuteNonQuery();

        AddMissingColumns(connection, columns);
    }

    /// <summary>
    /// Adds, to a table an older store already had, every column the model declares and the file does
    /// not hold. SQLite allows <c>ADD COLUMN</c> for exactly this purpose; a required column needs a
    /// default because the existing rows must be given one, so the type's zero value is used.
    /// </summary>
    private static void AddMissingColumns(SqliteConnection connection, IReadOnlyList<SchemaColumn> columns)
    {
        var present = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            if (!present.TryGetValue(column.Table, out var held))
            {
                held = ColumnsOf(connection, column.Table);
                present[column.Table] = held;

                // A table the model declares but this connection could not read is not ours to alter.
                if (held.Count == 0) continue;
            }

            if (!held.Add(column.Column)) continue;

            using var command = connection.CreateCommand();
            command.CommandText =
                $"ALTER TABLE \"{column.Table}\" ADD COLUMN \"{column.Column}\" {column.StoreType}"
                + (column.Nullable ? string.Empty : $" NOT NULL DEFAULT {column.Default}") + ";";

            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Another process added it between the read and the statement.
            }
        }
    }

    /// <summary>The columns a table physically holds, by name; empty when the table does not exist.</summary>
    private static HashSet<string> ColumnsOf(SqliteConnection connection, string table)
    {
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1)) held.Add(reader.GetString(1));
        }

        return held;
    }

    /// <summary>
    /// A context over an open connection, for code that wants the tables as entities instead of SQL text.
    /// The context does not own the connection: disposing it leaves the store's connection open.
    /// </summary>
    public static VisualMcpDbContext CreateContext(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var options = new DbContextOptionsBuilder<VisualMcpDbContext>().UseSqlite(connection).Options;
        return new VisualMcpDbContext(options);
    }

    /// <summary>The model's DDL, generated once: the script costs a model build and never changes at run time.</summary>
    private static (string[] Statements, string[] Tables, SchemaColumn[] Columns) Build()
    {
        // GenerateCreateScript only reads the model: it never opens this connection.
        var options = new DbContextOptionsBuilder<VisualMcpDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var context = new VisualMcpDbContext(options);
        var statements = Split(context.Database.GenerateCreateScript());

        var tables = statements
            .Where(statement => statement.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
            .Select(CreatedObject)
            .OfType<string>()
            .Append("ti_line")
            .ToArray();

        var columns = context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetTableName() is { } table
                ? entity.GetProperties().Select(property => new SchemaColumn(
                    table,
                    property.GetColumnName(),
                    property.GetColumnType(),
                    property.IsNullable,
                    DefaultOf(property)))
                : [])
            .ToArray();

        return (statements, tables, columns);
    }

    /// <summary>
    /// The literal an existing row is given when a required column is added: the type's zero value,
    /// quoted for text. Only the column types this model adds to a live table need one.
    /// </summary>
    private static string DefaultOf(Microsoft.EntityFrameworkCore.Metadata.IProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        if (type == typeof(string)) return "''";
        if (type == typeof(bool)) return "0";
        if (type == typeof(Guid)) return "''";
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return "''";
        if (type.IsEnum) return "0";
        if (type.IsPrimitive || type == typeof(decimal)) return "0";

        return "''";
    }

    /// <summary>One column the model declares: the table it lives in, its SQL type and whether it may be null.</summary>
    private sealed record SchemaColumn(string Table, string Column, string StoreType, bool Nullable, string Default);

    /// <summary>The DDL the model generates, one statement per entry.</summary>
    private static string[] Split(string script)
    {
        var statements = new List<string>();
        var start = 0;

        for (var index = 0; index < script.Length; index++)
        {
            if (script[index] != ';') continue;
            Add(statements, script[start..(index + 1)]);
            start = index + 1;
        }

        Add(statements, script[start..]);
        return statements.ToArray();

        static void Add(List<string> statements, string text)
        {
            var statement = text.Trim();
            if (statement.Length > 0) statements.Add(statement);
        }
    }

    /// <summary>The table or index a CREATE statement creates, or null for a statement it does not recognise.</summary>
    private static string? CreatedObject(string statement)
    {
        var text = statement.AsSpan().TrimStart();

        if (text.StartsWith("CREATE UNIQUE INDEX ".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return Identifier(text["CREATE UNIQUE INDEX ".Length..]);

        if (text.StartsWith("CREATE INDEX ".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return Identifier(text["CREATE INDEX ".Length..]);

        if (text.StartsWith("CREATE TABLE ".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return Identifier(text["CREATE TABLE ".Length..]);

        return null;
    }

    private static string? Identifier(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            var closing = text[1..].IndexOf('"');
            return closing <= 0 ? null : text.Slice(1, closing).ToString();
        }

        var length = 0;
        while (length < text.Length && !char.IsWhiteSpace(text[length]) && text[length] != '(') length++;
        return length == 0 ? null : text[..length].ToString();
    }

    /// <summary>Every table and index name in the file, which is what makes the creation incremental.</summary>
    private static HashSet<string> ExistingObjects(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index');";
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));

        return names;
    }
}
