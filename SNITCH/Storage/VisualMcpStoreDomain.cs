using Microsoft.Data.Sqlite;

namespace Snitch.Storage;

/// <summary>
/// Prepares one of the per-domain databases (<c>board.db</c>, <c>metacode.db</c>) the first time it is
/// needed. The file is built under a staging name — schema ensured, the legacy tables an older build
/// wrote into <c>visualmcp.db</c> copied through an ATTACH, every row count verified — and only then
/// moved into place, so a failure at any point leaves either nothing or a complete copy behind, and
/// the next call simply retries from scratch.
/// </summary>
public static class VisualMcpStoreDomain
{
    public static void Prepare(
        string domainPath, string mainPath, IReadOnlyList<string> tables, Action<SqliteConnection> ensureSchema)
    {
        ArgumentNullException.ThrowIfNull(domainPath);
        ArgumentNullException.ThrowIfNull(mainPath);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(ensureSchema);

        if (File.Exists(domainPath)) return;

        var staging = domainPath + $".new-{Environment.ProcessId}";

        try
        {
            File.Delete(staging);
            File.Delete(staging + "-wal");
            File.Delete(staging + "-shm");

            // Pooling=False: a pooled connection keeps its file handle open after Dispose, which would
            // leave the staging file locked for the move (and for the retry's deletes).
            using (var domain = new SqliteConnection($"Data Source={staging};Pooling=False"))
            {
                domain.Open();
                // The default rollback journal, not WAL: the copy must be inside the file itself,
                // because only the main file travels with the move that publishes it.
                Execute(domain, "PRAGMA journal_mode=DELETE;");
                Execute(domain, "PRAGMA busy_timeout=5000;");
                Execute(domain, $"ATTACH DATABASE '{SqlQuote(mainPath)}' AS legacy;");

                // The domain's full schema first: the copy inserts into these tables, and a fresh
                // workspace has no legacy tables to bring across at all.
                ensureSchema(domain);

                using (var transaction = domain.BeginTransaction())
                {
                    foreach (var table in tables)
                    {
                        // A table the old single-file build never wrote carries nothing across.
                        var exists = Scalar(
                            domain,
                            $"SELECT COUNT(*) FROM legacy.sqlite_master WHERE type = 'table' AND name = '{SqlQuote(table)}';");
                        if (exists == 0) continue;

                        CopyTable(domain, table);

                        // The copy is only verified when both sides report the same row count; a mismatch
                        // aborts the transaction and the whole preparation retries on the next call.
                        var (domainCount, legacyCount) = Counts(domain, table);
                        if (domainCount != legacyCount)
                            throw new InvalidOperationException(
                                $"The copy of '{table}' is incomplete: {domainCount} of {legacyCount} rows.");
                    }

                    // Dispose without Commit is a rollback: the copy becomes durable here or nowhere.
                    transaction.Commit();
                }

                Execute(domain, "DETACH DATABASE legacy;");
            }

            File.Move(staging, domainPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(staging); } catch { /* best effort */ }
            try { File.Delete(staging + "-wal"); } catch { /* best effort */ }
            try { File.Delete(staging + "-shm"); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Removes the domain tables an older build left in the main database — called only after their
    /// verified copy lives in its own file. Each drop stands alone: one that cannot run yet is skipped,
    /// and the next preparation pass removes it.
    /// </summary>
    public static void DropLegacyTables(SqliteConnection connection, IReadOnlyList<string> tables)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tables);

        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS \"{SqlQuote(table)}\";";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Copies one legacy table by NAME, not position: a table an older model wrote may hold its
    /// columns in a different order than today's (an old revision had <c>kind</c> before <c>name</c>,
    /// and columns added in place trail the ones the model declares first). A positional
    /// <c>SELECT *</c> would land values in the wrong columns — silently where the table has no key.
    /// Columns the legacy table does not have yet are left to their defaults.
    /// </summary>
    private static void CopyTable(SqliteConnection domain, string table)
    {
        var fresh = ColumnNames(domain, "main", table);
        var legacy = ColumnNames(domain, "legacy", table);
        var shared = fresh.Where(legacy.Contains).ToList();
        if (shared.Count == 0) return;

        var list = string.Join(", ", shared.Select(name => "\"" + name.Replace("\"", "\"\"") + "\""));
        Execute(domain, $"INSERT INTO \"{table.Replace("\"", "\"\"")}\" ({list}) SELECT {list} FROM legacy.\"{table.Replace("\"", "\"\"")}\";");
    }

    /// <summary>The columns a table holds, in file order, through <c>PRAGMA &lt;schema&gt;.table_info</c>.</summary>
    private static List<string> ColumnNames(SqliteConnection domain, string schema, string table)
    {
        var names = new List<string>();

        using var command = domain.CreateCommand();
        command.CommandText = $"PRAGMA {schema}.table_info(\"{table.Replace("\"", "\"\"")}\");";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1)) names.Add(reader.GetString(1));
        }

        return names;
    }

    private static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        var result = command.ExecuteScalar();
        return result is long value ? value : Convert.ToInt64(result ?? 0L);
    }

    private static (long InDomain, long InLegacy) Counts(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT (SELECT COUNT(*) FROM \"{SqlQuote(table)}\"), (SELECT COUNT(*) FROM legacy.\"{SqlQuote(table)}\");";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static string SqlQuote(string identifier) => identifier.Replace("'", "''");
}
