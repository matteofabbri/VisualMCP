using System.Text;
using Microsoft.Data.Sqlite;
using Snitch.Storage;

namespace Snitch.Planning;

/// <summary>
/// A Jira-style board stored in the solution's SQLite database
/// (<c>&lt;solution directory&gt;/.visualMCP/visualmcp.db</c>): items move across the five columns,
/// carry priority, labels and an assignee, take comments, and record every field change in a
/// history table so the state of an item is never a guess.
/// </summary>
public interface IBoardService
{
    Task<object> AddAsync(string title, string? description, string? status, string? priority,
        string[]? labels, string? assignee);

    Task<object> UpdateAsync(int id, string? title, string? description, string? priority,
        string[]? labels, string? assignee);

    Task<object> MoveAsync(int id, string status, int? position);
    Task<object> CommentAsync(int id, string body, string? author);
    Task<object> DeleteAsync(int id);
    Task<object> ShowAsync(int id);
    Task<object> ListAsync(string? status, string? label, string? assignee, string? priority, string? text);
    Task<object> BoardAsync();

    /// <summary>
    /// Create many cards in one pass, keyed on the title so a re-run is a no-op — or an update,
    /// when <paramref name="existing"/> is "update". This is the bridge between a written backlog
    /// (a gap inventory, a plan) and the board: the document stays the source of truth and the board
    /// mirrors it without duplicates.
    /// </summary>
    Task<object> ImportAsync(IReadOnlyList<CardDraft> cards, string? existing);

    /// <summary>The board as Markdown, one section per column — for a document, a PR or a plan.</summary>
    Task<object> ExportAsync(string? status);
}

public sealed class BoardService : IBoardService
{
    /// <summary>The columns, left to right. This order is the board's contract.</summary>
    public static readonly string[] Statuses = ["backlog", "todo", "in_progress", "review", "done"];

    private static readonly string[] Priorities = ["lowest", "low", "medium", "high", "highest"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS board_item (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            title       TEXT NOT NULL,
            description TEXT NULL,
            status      TEXT NOT NULL,
            priority    TEXT NOT NULL,
            labels      TEXT NULL,
            assignee    TEXT NULL,
            position    INTEGER NOT NULL,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS board_item_status ON board_item (status, position);

        CREATE TABLE IF NOT EXISTS board_comment (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id    INTEGER NOT NULL,
            author     TEXT NULL,
            body       TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS board_comment_item ON board_comment (item_id);

        CREATE TABLE IF NOT EXISTS board_history (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id   INTEGER NOT NULL,
            field     TEXT NOT NULL,
            old_value TEXT NULL,
            new_value TEXT NULL,
            at        TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS board_history_item ON board_history (item_id);
        """;

    private readonly IVisualMcpStore _store;

    public BoardService(IVisualMcpStore store) => _store = store;

    // ── items ────────────────────────────────────────────────────────────────
    public Task<object> AddAsync(string title, string? description, string? status, string? priority,
        string[]? labels, string? assignee)
    {
        if (string.IsNullOrWhiteSpace(title)) return Fail("A title is required.");

        var column = NormalizeStatus(status);
        if (column is null) return Fail(StatusError(status));

        var level = NormalizePriority(priority);
        if (level is null) return Fail(PriorityError(priority));

        return With(connection =>
        {
            var now = Stamped();
            var position = NextPosition(connection, column);

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO board_item (title, description, status, priority, labels, assignee, position, created_at, updated_at)
                VALUES ($title, $description, $status, $priority, $labels, $assignee, $position, $now, $now);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$title", title.Trim());
            command.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", column);
            command.Parameters.AddWithValue("$priority", level);
            command.Parameters.AddWithValue("$labels", (object?)Join(labels) ?? DBNull.Value);
            command.Parameters.AddWithValue("$assignee", (object?)assignee ?? DBNull.Value);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$now", now);

            var id = Convert.ToInt32(command.ExecuteScalar());
            Record(connection, id, "created", null, column, now);

            return (object)new { added = true, id, status = column, priority = level, position, item = ReadItem(connection, id) };
        });
    }

    public Task<object> UpdateAsync(int id, string? title, string? description, string? priority,
        string[]? labels, string? assignee)
    {
        if (id < 1) return Fail("A valid item id is required.");

        var level = priority is null ? null : NormalizePriority(priority);
        if (priority is not null && level is null) return Fail(PriorityError(priority));

        return With(connection =>
        {
            var current = ReadItem(connection, id);
            if (current is null) return (object)new { error = $"No board item with id {id}." };

            var now = Stamped();
            var changes = new List<object>();

            void Apply(string field, string? value)
            {
                if (value is null) return;
                var old = Field(connection, id, field);
                if (string.Equals(old, value, StringComparison.Ordinal)) return;

                using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE board_item SET {field} = $value, updated_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                Record(connection, id, field, old, value, now);
                changes.Add(new { field, from = old, to = value });
            }

            Apply("title", title?.Trim());
            Apply("description", description);
            Apply("priority", level);
            Apply("labels", labels is null ? null : Join(labels));
            Apply("assignee", assignee);

            if (changes.Count == 0)
                return (object)new { updated = false, id, note = "Nothing to change: the values already match." };

            return new { updated = true, id, changes, item = ReadItem(connection, id) };
        });
    }

    public Task<object> MoveAsync(int id, string status, int? position)
    {
        if (id < 1) return Fail("A valid item id is required.");

        var column = NormalizeStatus(status);
        if (column is null) return Fail(StatusError(status));

        return With(connection =>
        {
            var item = ReadItem(connection, id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            var now = Stamped();
            var from = Field(connection, id, "status");
            var target = position ?? NextPosition(connection, column);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE board_item SET status = $status, position = $position, updated_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$status", column);
                command.Parameters.AddWithValue("$position", target);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            if (!string.Equals(from, column, StringComparison.Ordinal))
                Record(connection, id, "status", from, column, now);
            Record(connection, id, "position", null, target.ToString(), now);

            Renumber(connection, column);
            if (from is not null && !string.Equals(from, column, StringComparison.Ordinal))
                Renumber(connection, from);

            return new
            {
                moved = true,
                id,
                from,
                to = column,
                position = target,
                item = ReadItem(connection, id),
            };
        });
    }

    public Task<object> CommentAsync(int id, string body, string? author)
    {
        if (id < 1) return Fail("A valid item id is required.");
        if (string.IsNullOrWhiteSpace(body)) return Fail("The comment body is required.");

        return With(connection =>
        {
            if (ReadItem(connection, id) is null) return (object)new { error = $"No board item with id {id}." };

            var now = Stamped();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO board_comment (item_id, author, body, created_at) VALUES ($item, $author, $body, $now);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$item", id);
            command.Parameters.AddWithValue("$author", (object?)author ?? DBNull.Value);
            command.Parameters.AddWithValue("$body", body.Trim());
            command.Parameters.AddWithValue("$now", now);
            var commentId = Convert.ToInt32(command.ExecuteScalar());

            using (var touch = connection.CreateCommand())
            {
                touch.CommandText = "UPDATE board_item SET updated_at = $now WHERE id = $id;";
                touch.Parameters.AddWithValue("$now", now);
                touch.Parameters.AddWithValue("$id", id);
                touch.ExecuteNonQuery();
            }

            return (object)new
            {
                commented = true,
                item = id,
                comment = new { id = commentId, author, body = body.Trim(), createdAt = now },
                commentCount = Comments(connection, id).Count,
            };
        });
    }

    public Task<object> DeleteAsync(int id)
    {
        if (id < 1) return Fail("A valid item id is required.");

        return With(connection =>
        {
            var item = ReadItem(connection, id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            foreach (var table in new[] { "board_comment", "board_history", "board_item" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table} WHERE {(table == "board_item" ? "id" : "item_id")} = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            return (object)new { deleted = true, id, item };
        });
    }

    public Task<object> ShowAsync(int id)
    {
        if (id < 1) return Fail("A valid item id is required.");

        return With(connection =>
        {
            var item = ReadItem(connection, id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            return new
            {
                item,
                comments = Comments(connection, id),
                history = History(connection, id),
            };
        });
    }

    public Task<object> ListAsync(string? status, string? label, string? assignee, string? priority, string? text)
    {
        string? column = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            column = NormalizeStatus(status);
            if (column is null) return Fail(StatusError(status));
        }

        string? level = null;
        if (!string.IsNullOrWhiteSpace(priority))
        {
            level = NormalizePriority(priority);
            if (level is null) return Fail(PriorityError(priority));
        }

        return With(connection =>
        {
            var where = new List<string>();
            using var command = connection.CreateCommand();

            if (column is not null) { where.Add("status = $status"); command.Parameters.AddWithValue("$status", column); }
            if (level is not null) { where.Add("priority = $priority"); command.Parameters.AddWithValue("$priority", level); }
            if (!string.IsNullOrWhiteSpace(assignee)) { where.Add("assignee = $assignee"); command.Parameters.AddWithValue("$assignee", assignee); }
            if (!string.IsNullOrWhiteSpace(label)) { where.Add("(',' || COALESCE(labels,'') || ',') LIKE $label"); command.Parameters.AddWithValue("$label", $"%,{label!.Trim()},%"); }
            if (!string.IsNullOrWhiteSpace(text))
            {
                where.Add("(title LIKE $text OR COALESCE(description,'') LIKE $text)");
                command.Parameters.AddWithValue("$text", $"%{text!.Trim()}%");
            }

            command.CommandText = "SELECT id FROM board_item" +
                                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                                  " ORDER BY CASE status" +
                                  " WHEN 'backlog' THEN 0 WHEN 'todo' THEN 1 WHEN 'in_progress' THEN 2" +
                                  " WHEN 'review' THEN 3 WHEN 'done' THEN 4 ELSE 5 END, position, id;";

            var ids = new List<long>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetInt64(0));

            var items = ids.Select(id => ReadItem(connection, (int)id)).OfType<object>().ToList();
            return (object)new
            {
                count = items.Count,
                filters = new { status = column, priority = level, assignee, label, text },
                items,
            };
        });
    }

    public Task<object> BoardAsync() => With(connection =>
    {
        var columns = Statuses.Select(status =>
        {
            var items = Items(connection, status);
            return new
            {
                status,
                count = items.Count,
                items,
            };
        }).ToList();

        var totals = Statuses.ToDictionary(status => status, status => Count(connection, status));

        return (object)new
        {
            columns,
            totals,
            itemCount = totals.Values.Sum(),
            doneShare = totals.Values.Sum() == 0 ? (double?)null : Math.Round(100.0 * totals["done"] / totals.Values.Sum(), 1),
            note = "Columns left to right: " + string.Join(" → ", Statuses) + ".",
        };
    });

    public Task<object> ImportAsync(IReadOnlyList<CardDraft> cards, string? existing)
    {
        if (cards is null || cards.Count == 0) return Fail("At least one card is required.");

        var policy = (existing ?? "skip").Trim().ToLowerInvariant();
        if (policy is not ("skip" or "update" or "duplicate"))
            return Fail("'existing' must be one of: skip (default), update, duplicate.");

        return With(connection =>
        {
            var imported = new List<object>();
            var updated = new List<object>();
            var skipped = new List<object>();
            var errors = new List<object>();

            for (var index = 0; index < cards.Count; index++)
            {
                var card = cards[index];
                if (string.IsNullOrWhiteSpace(card.Title))
                {
                    errors.Add(new { index, error = "A title is required." });
                    continue;
                }

                var title = card.Title.Trim();
                var column = NormalizeStatus(card.Status);
                if (column is null)
                {
                    errors.Add(new { index, title, error = StatusError(card.Status) });
                    continue;
                }

                var level = NormalizePriority(card.Priority);
                if (level is null)
                {
                    errors.Add(new { index, title, error = PriorityError(card.Priority) });
                    continue;
                }

                var now = Stamped();
                var currentId = FindByTitle(connection, title);

                if (currentId is int known)
                {
                    if (policy == "skip")
                    {
                        skipped.Add(new { index, id = known, title });
                        continue;
                    }

                    if (policy == "update")
                    {
                        var changes = new List<object>();
                        UpdateField(connection, known, "description", card.Description, changes, now);
                        UpdateField(connection, known, "status", column, changes, now);
                        UpdateField(connection, known, "priority", level, changes, now);
                        UpdateField(connection, known, "labels", Join(card.Labels), changes, now);
                        UpdateField(connection, known, "assignee", card.Assignee, changes, now);
                        updated.Add(new { index, id = known, title, changes });
                        continue;
                    }
                }

                var position = NextPosition(connection, column);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO board_item (title, description, status, priority, labels, assignee, position, created_at, updated_at)
                    VALUES ($title, $description, $status, $priority, $labels, $assignee, $position, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$description", (object?)card.Description ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", column);
                command.Parameters.AddWithValue("$priority", level);
                command.Parameters.AddWithValue("$labels", (object?)Join(card.Labels) ?? DBNull.Value);
                command.Parameters.AddWithValue("$assignee", (object?)card.Assignee ?? DBNull.Value);
                command.Parameters.AddWithValue("$position", position);
                command.Parameters.AddWithValue("$now", now);

                var id = Convert.ToInt32(command.ExecuteScalar());
                Record(connection, id, "created", null, column, now);
                imported.Add(new { index, id, title, status = column, priority = level });
            }

            return (object)new
            {
                imported = imported.Count,
                updated = updated.Count,
                skipped = skipped.Count,
                failed = errors.Count,
                policy,
                items = imported,
                updatedItems = updated,
                skippedItems = skipped,
                errors,
            };
        });
    }

    public Task<object> ExportAsync(string? status)
    {
        string? column = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            column = NormalizeStatus(status);
            if (column is null) return Fail(StatusError(status));
        }

        return With(connection =>
        {
            var columns = column is null ? Statuses : [column];
            var markdown = new StringBuilder();
            markdown.AppendLine("# Board");
            var counts = new Dictionary<string, int>();

            foreach (var name in columns)
            {
                var rows = ExportRows(connection, name);
                counts[name] = rows.Count;
                markdown.AppendLine();
                markdown.AppendLine($"## {name} ({rows.Count})");

                if (rows.Count == 0)
                {
                    markdown.AppendLine();
                    markdown.AppendLine("_Empty._");
                    continue;
                }

                foreach (var row in rows)
                {
                    markdown.AppendLine();
                    var labels = row.Labels.Length == 0 ? string.Empty : " — `" + string.Join(", ", row.Labels) + "`";
                    var assignee = string.IsNullOrWhiteSpace(row.Assignee) ? string.Empty : $", {row.Assignee}";
                    markdown.AppendLine($"- **#{row.Id}** {row.Title} — {row.Priority}{assignee}{labels}");

                    if (!string.IsNullOrWhiteSpace(row.Description))
                    {
                        foreach (var line in row.Description.Split('\n'))
                            markdown.AppendLine("  " + line.TrimEnd('\r'));
                    }
                }
            }

            return (object)new { markdown = markdown.ToString(), counts };
        });
    }

    private sealed record ExportRow(int Id, string Title, string? Description, string Priority, string[] Labels, string? Assignee);

    private static List<ExportRow> ExportRows(SqliteConnection connection, string status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, description, priority, labels, assignee
            FROM board_item WHERE status = $status ORDER BY position, id;
            """;
        command.Parameters.AddWithValue("$status", status);

        var rows = new List<ExportRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ExportRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                Split(reader.IsDBNull(4) ? null : reader.GetString(4)).ToArray(),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static int? FindByTitle(SqliteConnection connection, string title)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM board_item WHERE title = $title ORDER BY id LIMIT 1;";
        command.Parameters.AddWithValue("$title", title);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    /// <summary>Set one field when the draft supplies it, and record the history entry when it actually changes.</summary>
    private static void UpdateField(SqliteConnection connection, int id, string field, string? value,
        List<object> changes, string now)
    {
        if (value is null) return;

        var old = Field(connection, id, field);
        if (string.Equals(old, value, StringComparison.Ordinal)) return;

        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE board_item SET {field} = $value, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        Record(connection, id, field, old, value, now);
        changes.Add(new { field, from = old, to = value });
    }

    // ── reading ──────────────────────────────────────────────────────────────
    private object? ReadItem(SqliteConnection connection, int id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, description, status, priority, labels, assignee, position, created_at, updated_at,
                   (SELECT COUNT(*) FROM board_comment WHERE item_id = board_item.id)
            FROM board_item WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new
        {
            id = reader.GetInt32(0),
            title = reader.GetString(1),
            description = reader.IsDBNull(2) ? null : reader.GetString(2),
            status = reader.GetString(3),
            priority = reader.GetString(4),
            labels = Split(reader.IsDBNull(5) ? null : reader.GetString(5)),
            assignee = reader.IsDBNull(6) ? null : reader.GetString(6),
            position = reader.GetInt32(7),
            createdAt = reader.GetString(8),
            updatedAt = reader.GetString(9),
            commentCount = reader.GetInt32(10),
        };
    }

    private List<object> Items(SqliteConnection connection, string status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM board_item WHERE status = $status ORDER BY position, id;";
        command.Parameters.AddWithValue("$status", status);

        var ids = new List<long>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetInt64(0));

        return ids.Select(id => ReadItem(connection, (int)id)).OfType<object>().ToList();
    }

    private static int Count(SqliteConnection connection, string status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM board_item WHERE status = $status;";
        command.Parameters.AddWithValue("$status", status);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private List<object> Comments(SqliteConnection connection, int id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, author, body, created_at FROM board_comment WHERE item_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", id);

        var comments = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            comments.Add(new
            {
                id = reader.GetInt32(0),
                author = reader.IsDBNull(1) ? null : reader.GetString(1),
                body = reader.GetString(2),
                createdAt = reader.GetString(3),
            });
        }

        return comments;
    }

    private List<object> History(SqliteConnection connection, int id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT field, old_value, new_value, at FROM board_history WHERE item_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", id);

        var history = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            history.Add(new
            {
                field = reader.GetString(0),
                from = reader.IsDBNull(1) ? null : reader.GetString(1),
                to = reader.IsDBNull(2) ? null : reader.GetString(2),
                at = reader.GetString(3),
            });
        }

        return history;
    }

    private static string? Field(SqliteConnection connection, int id, string field)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {field} FROM board_item WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : value.ToString();
    }

    // ── writing helpers ──────────────────────────────────────────────────────
    private static int NextPosition(SqliteConnection connection, string status)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM board_item WHERE status = $status;";
        command.Parameters.AddWithValue("$status", status);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Rewrite one column's positions to 0..n-1, so a move never leaves holes or ties.</summary>
    private static void Renumber(SqliteConnection connection, string status)
    {
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM board_item WHERE status = $status ORDER BY position, id;";
        select.Parameters.AddWithValue("$status", status);

        var ids = new List<long>();
        using (var reader = select.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetInt64(0));

        for (var index = 0; index < ids.Count; index++)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE board_item SET position = $position WHERE id = $id;";
            update.Parameters.AddWithValue("$position", index);
            update.Parameters.AddWithValue("$id", ids[index]);
            update.ExecuteNonQuery();
        }
    }

    private static void Record(SqliteConnection connection, int id, string field, string? oldValue, string? newValue, string at)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO board_history (item_id, field, old_value, new_value, at)
            VALUES ($item, $field, $old, $new, $at);
            """;
        command.Parameters.AddWithValue("$item", id);
        command.Parameters.AddWithValue("$field", field);
        command.Parameters.AddWithValue("$old", (object?)oldValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$new", (object?)newValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at);
        command.ExecuteNonQuery();
    }

    // ── plumbing ─────────────────────────────────────────────────────────────
    private Task<object> With(Func<SqliteConnection, object> work)
    {
        var path = _store.EnsureDatabase();
        if (path is null)
            return Task.FromResult<object>(new
            {
                error = "No solution is loaded (or its folder is not writable), so there is no board database. " +
                        "Call load_solution first.",
            });

        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }

            return Task.FromResult(work(connection));
        }
        catch (SqliteException ex)
        {
            return Task.FromResult<object>(new { error = $"Board database error: {ex.Message}", database = path });
        }
    }

    private static Task<object> Fail(string error) => Task.FromResult<object>(new { error });

    private static string Stamped() => DateTimeOffset.UtcNow.ToString("O");

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return Statuses[0];
        var candidate = status.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return Statuses.FirstOrDefault(s => s == candidate);
    }

    private static string? NormalizePriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return "medium";
        var candidate = priority.Trim().ToLowerInvariant();
        return Priorities.FirstOrDefault(p => p == candidate);
    }

    private static string StatusError(string? status) =>
        $"'{status}' is not a column. Use one of: {string.Join(", ", Statuses)}.";

    private static string PriorityError(string? priority) =>
        $"'{priority}' is not a priority. Use one of: {string.Join(", ", Priorities)}.";

    private static string? Join(string[]? labels) =>
        labels is null || labels.Length == 0
            ? null
            : string.Join(',', labels.Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label.Trim()));

    private static List<string> Split(string? labels) =>
        string.IsNullOrWhiteSpace(labels)
            ? []
            : labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
