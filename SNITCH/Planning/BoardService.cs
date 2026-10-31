using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Storage;
using Snitch.Storage.Schema;

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

        return With(context =>
        {
            var now = DateTimeOffset.UtcNow;
            var item = new BoardItemRow
            {
                Title = title.Trim(),
                Description = description,
                Status = column!,
                Priority = level!,
                Labels = Join(labels),
                Assignee = assignee,
                Position = NextPosition(context, column!),
                CreatedAt = now,
                UpdatedAt = now,
            };

            context.BoardItems.Add(item);
            context.SaveChanges();

            Record(context, item.Id, "created", null, column, now);
            context.SaveChanges();

            return (object)new
            {
                added = true,
                id = item.Id,
                status = column,
                priority = level,
                position = item.Position,
                item = ReadItem(context, item.Id),
            };
        });
    }

    public Task<object> UpdateAsync(int id, string? title, string? description, string? priority,
        string[]? labels, string? assignee)
    {
        if (id < 1) return Fail("A valid item id is required.");

        var level = priority is null ? null : NormalizePriority(priority);
        if (priority is not null && level is null) return Fail(PriorityError(priority));

        return With(context =>
        {
            var item = context.BoardItems.FirstOrDefault(row => row.Id == id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            var now = DateTimeOffset.UtcNow;
            var changes = new List<object>();

            void Apply(string field, string? value)
            {
                if (value is null) return;
                var (old, set) = FieldAccess(item, field);
                if (string.Equals(old, value, StringComparison.Ordinal)) return;

                set(value);
                item.UpdatedAt = now;
                Record(context, id, field, old, value, now);
                changes.Add(new { field, from = old, to = value });
            }

            Apply("title", title?.Trim());
            Apply("description", description);
            Apply("priority", level);
            Apply("labels", labels is null ? null : Join(labels));
            Apply("assignee", assignee);

            if (changes.Count == 0)
                return (object)new { updated = false, id, note = "Nothing to change: the values already match." };

            context.SaveChanges();
            return new { updated = true, id, changes, item = ReadItem(context, id) };
        });
    }

    public Task<object> MoveAsync(int id, string status, int? position)
    {
        if (id < 1) return Fail("A valid item id is required.");

        var column = NormalizeStatus(status);
        if (column is null) return Fail(StatusError(status));

        return With(context =>
        {
            var item = context.BoardItems.FirstOrDefault(row => row.Id == id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            var now = DateTimeOffset.UtcNow;
            var from = item.Status;
            var target = position ?? NextPosition(context, column!);
            var statusChanged = !string.Equals(from, column, StringComparison.Ordinal);

            using var transaction = context.Database.BeginTransaction();

            item.Status = column!;
            item.Position = target;
            item.UpdatedAt = now;

            if (statusChanged)
                Record(context, id, "status", from, column, now);
            Record(context, id, "position", null, target.ToString(), now);

            // Renumbering reads the stored positions, so the move is saved first and rewritten after.
            context.SaveChanges();

            Renumber(context, column!);
            if (from is not null && statusChanged)
                Renumber(context, from);

            context.SaveChanges();
            transaction.Commit();

            return new
            {
                moved = true,
                id,
                from,
                to = column,
                position = target,
                item = ReadItem(context, id),
            };
        });
    }

    public Task<object> CommentAsync(int id, string body, string? author)
    {
        if (id < 1) return Fail("A valid item id is required.");
        if (string.IsNullOrWhiteSpace(body)) return Fail("The comment body is required.");

        return With(context =>
        {
            var item = context.BoardItems.FirstOrDefault(row => row.Id == id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            var now = DateTimeOffset.UtcNow;
            var comment = new BoardCommentRow
            {
                ItemId = id,
                Author = author,
                Body = body.Trim(),
                CreatedAt = now,
            };

            context.BoardComments.Add(comment);
            item.UpdatedAt = now;
            context.SaveChanges();

            return (object)new
            {
                commented = true,
                item = id,
                comment = new { id = comment.Id, author, body = body.Trim(), createdAt = now.ToString("O") },
                commentCount = context.BoardComments.Count(row => row.ItemId == id),
            };
        });
    }

    public Task<object> DeleteAsync(int id)
    {
        if (id < 1) return Fail("A valid item id is required.");

        return With(context =>
        {
            var item = ReadItem(context, id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            context.BoardComments.Where(row => row.ItemId == id).ExecuteDelete();
            context.BoardHistory.Where(row => row.ItemId == id).ExecuteDelete();
            context.BoardItems.Where(row => row.Id == id).ExecuteDelete();

            return (object)new { deleted = true, id, item };
        });
    }

    public Task<object> ShowAsync(int id)
    {
        if (id < 1) return Fail("A valid item id is required.");

        return With(context =>
        {
            var item = ReadItem(context, id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            return new
            {
                item,
                comments = Comments(context, id),
                history = History(context, id),
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

        return With(context =>
        {
            var query = context.BoardItems.AsNoTracking().AsQueryable();

            if (column is not null) query = query.Where(item => item.Status == column);
            if (level is not null) query = query.Where(item => item.Priority == level);
            if (!string.IsNullOrWhiteSpace(assignee)) query = query.Where(item => item.Assignee == assignee);

            if (!string.IsNullOrWhiteSpace(label))
            {
                var needle = $"%,{label.Trim()},%";
                query = query.Where(item => EF.Functions.Like("," + (item.Labels ?? string.Empty) + ",", needle));
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                var needle = $"%{text.Trim()}%";
                query = query.Where(item =>
                    EF.Functions.Like(item.Title, needle) ||
                    EF.Functions.Like(item.Description ?? string.Empty, needle));
            }

            var rows = query
                .Select(item => new
                {
                    Item = item,
                    CommentCount = context.BoardComments.Count(comment => comment.ItemId == item.Id),
                })
                .ToList();

            var items = rows
                .OrderBy(row => Array.IndexOf(Statuses, row.Item.Status))
                .ThenBy(row => row.Item.Position)
                .ThenBy(row => row.Item.Id)
                .Select(row => Shape(row.Item, row.CommentCount))
                .ToList();

            return (object)new
            {
                count = items.Count,
                filters = new { status = column, priority = level, assignee, label, text },
                items,
            };
        });
    }

    public Task<object> BoardAsync() => With(context =>
    {
        var columns = Statuses.Select(status =>
        {
            var items = Items(context, status);
            return new
            {
                status,
                count = items.Count,
                items,
            };
        }).ToList();

        var totals = columns.ToDictionary(column => column.status, column => column.count);

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

        return With(context =>
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

                var now = DateTimeOffset.UtcNow;
                var current = FindByTitle(context, title);

                if (current is not null)
                {
                    if (policy == "skip")
                    {
                        skipped.Add(new { index, id = current.Id, title });
                        continue;
                    }

                    if (policy == "update")
                    {
                        var changes = new List<object>();
                        UpdateField(context, current, "description", card.Description, changes, now);
                        UpdateField(context, current, "status", column, changes, now);
                        UpdateField(context, current, "priority", level, changes, now);
                        UpdateField(context, current, "labels", Join(card.Labels), changes, now);
                        UpdateField(context, current, "assignee", card.Assignee, changes, now);
                        context.SaveChanges();
                        updated.Add(new { index, id = current.Id, title, changes });
                        continue;
                    }
                }

                var item = new BoardItemRow
                {
                    Title = title,
                    Description = card.Description,
                    Status = column,
                    Priority = level,
                    Labels = Join(card.Labels),
                    Assignee = card.Assignee,
                    Position = NextPosition(context, column),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                context.BoardItems.Add(item);
                context.SaveChanges();

                Record(context, item.Id, "created", null, column, now);
                context.SaveChanges();
                imported.Add(new { index, id = item.Id, title, status = column, priority = level });
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

        return With(context =>
        {
            var columns = column is null ? Statuses : [column];
            var markdown = new StringBuilder();
            markdown.AppendLine("# Board");
            var counts = new Dictionary<string, int>();

            foreach (var name in columns)
            {
                var rows = ExportRows(context, name);
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

    private static List<ExportRow> ExportRows(VisualMcpDbContext context, string status)
    {
        return context.BoardItems
            .AsNoTracking()
            .Where(item => item.Status == status)
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Title, item.Description, item.Priority, item.Labels, item.Assignee })
            .ToList()
            .Select(item => new ExportRow(
                item.Id, item.Title, item.Description, item.Priority, Split(item.Labels).ToArray(), item.Assignee))
            .ToList();
    }

    private static BoardItemRow? FindByTitle(VisualMcpDbContext context, string title)
    {
        return context.BoardItems
            .Where(item => item.Title == title)
            .OrderBy(item => item.Id)
            .FirstOrDefault();
    }

    /// <summary>Set one field when the draft supplies it, and record the history entry when it actually changes.</summary>
    private static void UpdateField(VisualMcpDbContext context, BoardItemRow item, string field, string? value,
        List<object> changes, DateTimeOffset at)
    {
        if (value is null) return;

        var (old, set) = FieldAccess(item, field);
        if (string.Equals(old, value, StringComparison.Ordinal)) return;

        set(value);
        item.UpdatedAt = at;
        Record(context, item.Id, field, old, value, at);
        changes.Add(new { field, from = old, to = value });
    }

    /// <summary>The property a board field names: callers keep passing a field name, but nothing builds SQL from it.</summary>
    private static (string? Old, Action<string?> Set) FieldAccess(BoardItemRow item, string field) => field switch
    {
        "title" => (item.Title, value => item.Title = value!),
        "description" => (item.Description, value => item.Description = value),
        "status" => (item.Status, value => item.Status = value!),
        "priority" => (item.Priority, value => item.Priority = value!),
        "labels" => (item.Labels, value => item.Labels = value),
        "assignee" => (item.Assignee, value => item.Assignee = value),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown board field."),
    };

    // ── reading ──────────────────────────────────────────────────────────────
    private static object? ReadItem(VisualMcpDbContext context, int id)
    {
        var row = context.BoardItems
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                Item = item,
                CommentCount = context.BoardComments.Count(comment => comment.ItemId == item.Id),
            })
            .FirstOrDefault();

        return row is null ? null : Shape(row.Item, row.CommentCount);
    }

    /// <summary>The JSON shape of one item, shared by every read path so the responses cannot drift apart.</summary>
    private static object Shape(BoardItemRow item, int commentCount) => new
    {
        id = item.Id,
        title = item.Title,
        description = item.Description,
        status = item.Status,
        priority = item.Priority,
        labels = Split(item.Labels),
        assignee = item.Assignee,
        position = item.Position,
        createdAt = item.CreatedAt.ToUniversalTime().ToString("O"),
        updatedAt = item.UpdatedAt.ToUniversalTime().ToString("O"),
        commentCount,
    };

    private static List<object> Items(VisualMcpDbContext context, string status)
    {
        var rows = context.BoardItems
            .AsNoTracking()
            .Where(item => item.Status == status)
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                Item = item,
                CommentCount = context.BoardComments.Count(comment => comment.ItemId == item.Id),
            })
            .ToList();

        return rows.Select(row => Shape(row.Item, row.CommentCount)).ToList();
    }

    private static List<object> Comments(VisualMcpDbContext context, int id)
    {
        return context.BoardComments
            .AsNoTracking()
            .Where(comment => comment.ItemId == id)
            .OrderBy(comment => comment.Id)
            .Select(comment => new { comment.Id, comment.Author, comment.Body, comment.CreatedAt })
            .ToList()
            .Select(comment => (object)new
            {
                id = comment.Id,
                author = comment.Author,
                body = comment.Body,
                createdAt = comment.CreatedAt.ToUniversalTime().ToString("O"),
            })
            .ToList();
    }

    private static List<object> History(VisualMcpDbContext context, int id)
    {
        return context.BoardHistory
            .AsNoTracking()
            .Where(entry => entry.ItemId == id)
            .OrderBy(entry => entry.Id)
            .Select(entry => new { entry.Field, entry.OldValue, entry.NewValue, entry.At })
            .ToList()
            .Select(entry => (object)new
            {
                field = entry.Field,
                from = entry.OldValue,
                to = entry.NewValue,
                at = entry.At.ToUniversalTime().ToString("O"),
            })
            .ToList();
    }

    // ── writing helpers ──────────────────────────────────────────────────────
    private static int NextPosition(VisualMcpDbContext context, string status)
    {
        var highest = context.BoardItems
            .Where(item => item.Status == status)
            .Max(item => (int?)item.Position);

        return (highest ?? -1) + 1;
    }

    /// <summary>Rewrite one column's positions to 0..n-1, so a move never leaves holes or ties.</summary>
    private static void Renumber(VisualMcpDbContext context, string status)
    {
        var items = context.BoardItems
            .Where(item => item.Status == status)
            .OrderBy(item => item.Position)
            .ThenBy(item => item.Id)
            .ToList();

        for (var index = 0; index < items.Count; index++)
            items[index].Position = index;
    }

    private static void Record(VisualMcpDbContext context, int id, string field, string? oldValue, string? newValue, DateTimeOffset at)
    {
        context.BoardHistory.Add(new BoardHistoryRow
        {
            ItemId = id,
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            At = at,
        });
    }

    // ── plumbing ─────────────────────────────────────────────────────────────
    private Task<object> With(Func<VisualMcpDbContext, object> work)
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
            VisualMcpSchema.Ensure(connection);

            using var context = VisualMcpSchema.CreateContext(connection);
            return Task.FromResult(work(context));
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        {
            return Task.FromResult<object>(new { error = $"Board database error: {ex.Message}", database = path });
        }
    }

    private static Task<object> Fail(string error) => Task.FromResult<object>(new { error });

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
