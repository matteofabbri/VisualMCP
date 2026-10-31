using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snitch.Storage;
using Snitch.Storage.Schema;

namespace Snitch.Planning;

/// <summary>
/// The board in one small block: what an agent needs to see attached to another answer. Null is the
/// answer of an empty board — there is nothing to point at yet, and attaching "0 cards" to every
/// documentation read would teach the reader to ignore it.
/// </summary>
public sealed record BoardSummary(
    IReadOnlyDictionary<string, int> Totals,
    int OpenCount,
    int ItemCount,
    BoardSummaryNext? Next,
    string Hint,
    int MemoryCount = 0,
    IReadOnlyList<string>? MemoryPinned = null);

/// <summary>The outcome of one <c>board_remember</c>: what it wrote and what it replaced.</summary>
public sealed record BoardMemoryResult(
    bool Remembered,
    bool Unchanged,
    int? Id,
    string Topic,
    string? Kind,
    bool Replaced,
    int? PreviousId,
    string? Error = null)
{
    public static BoardMemoryResult Fail(string topic, string error) =>
        new(false, false, null, topic, null, false, null, error);
}

/// <summary>The card a session should pick up, in the brief's own order.</summary>
public sealed record BoardSummaryNext(int Id, string Title, string Status, string Priority, int IdleDays);

/// <summary>
/// A Jira-style board stored in the solution's SQLite database
/// (<c>&lt;solution directory&gt;/.visualMCP/visualmcp.db</c>): items move across the five columns,
/// carry priority, labels and an assignee, take comments, and record every field change in a
/// history table so the state of an item is never a guess.
/// </summary>
public interface IBoardService
{
    Task<object> AddAsync(string title, string? description, string? status, string? priority,
        string[]? labels, string? assignee, string[]? steps = null);

    Task<object> UpdateAsync(int id, string? title, string? description, string? priority,
        string[]? labels, string? assignee);

    Task<object> MoveAsync(int id, string status, int? position, string? note = null, string? author = null);
    Task<object> CommentAsync(int id, string body, string? author);
    Task<object> DeleteAsync(int id);
    Task<object> ShowAsync(int id);
    Task<object> ListAsync(string? status, string? label, string? assignee, string? priority, string? text);
    Task<object> BoardAsync();

    /// <summary>
    /// The one call that answers "what is missing / what comes next": counts, the card to pick up, every
    /// open card with its age and a stale flag, and the cards that need a look. Compact on purpose.
    /// </summary>
    Task<object> BriefAsync(int staleDays);

    /// <summary>Counts plus the next card — small enough to attach to another answer (null when the board is empty).</summary>
    Task<BoardSummary?> SummaryAsync();

    /// <summary>
    /// Cross-checks every open card against THIS build: the tool names a card names are matched against
    /// <paramref name="knownTools"/> and the file paths it names are checked under the solution root.
    /// <paramref name="close"/> moves the cards whose named tools all exist to done, with the evidence
    /// recorded as a comment — a candidate is never closed silently.
    /// </summary>
    Task<object> AuditAsync(IReadOnlyCollection<string> knownTools, bool close, string? author);

    /// <summary>
    /// Create many cards in one pass, keyed on the title so a re-run is a no-op — or an update,
    /// when <paramref name="existing"/> is "update". This is the bridge between a written backlog
    /// (a gap inventory, a plan) and the board: the document stays the source of truth and the board
    /// mirrors it without duplicates.
    /// </summary>
    Task<object> ImportAsync(IReadOnlyList<CardDraft> cards, string? existing);

    /// <summary>The board as Markdown, one section per column — for a document, a PR or a plan.</summary>
    Task<object> ExportAsync(string? status);

    /// <summary>
    /// Write a durable note — a fact, a decision, a constraint — under a topic. One current row per topic:
    /// a new body supersedes the previous one instead of duplicating it, and an identical body from the
    /// same source is a no-op (which is what makes the import idempotent).
    /// </summary>
    Task<BoardMemoryResult> RememberAsync(string topic, string body, string? kind, string[]? labels,
        bool pinned, string? source);

    /// <summary>Find the current memories: exact <paramref name="topic"/>, or a LIKE over topic and body.</summary>
    Task<object> RecallAsync(string? topic, string? query, string? kind, string? label, bool pinnedOnly, int limit);

    /// <summary>Retire a topic: its versions stay as history, no row is current any more.</summary>
    Task<object> ForgetAsync(string topic);

    /// <summary>Tick the checklist a card's description holds (1-based steps), the one create_task_checklist writes.</summary>
    Task<object> TickPlanAsync(int id, int[]? done, int[]? notDone);
}

public sealed class BoardService : IBoardService
{
    /// <summary>The columns, left to right. This order is the board's contract.</summary>
    public static readonly string[] Statuses = ["backlog", "todo", "in_progress", "review", "done"];

    private static readonly string[] Priorities = ["lowest", "low", "medium", "high", "highest"];

    /// <summary>Tool-shaped identifiers: lowercase snake_case, which is the shape this server's names have.</summary>
    private static readonly Regex SnakeCase = new(
        @"(?<![A-Za-z0-9_])([a-z][a-z0-9]*(?:_[a-z0-9]+)+)(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A path with at least one folder segment and an extension (docs/gaps.md, SNITCH/Planning/BoardService.cs).</summary>
    private static readonly Regex FilePath = new(
        @"(?<![A-Za-z0-9_])((?:[A-Za-z0-9_.\-]+[\\/])+[A-Za-z0-9_.\-]+\.[A-Za-z0-9]{1,6})(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Board vocabulary that has the shape of a tool name but is not one.</summary>
    private static readonly HashSet<string> NotAName = new(StringComparer.OrdinalIgnoreCase)
    {
        "in_progress", "dry_run", "done", "todo", "backlog", "review",
    };

    /// <summary>
    /// Extensions worth checking on disk. A card quotes table names, PRAGMA flags and temp file names in
    /// the same backticks as paths; only a file that a reader could open is reported missing.
    /// </summary>
    private static readonly HashSet<string> CheckableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".md", ".json", ".ps1", ".sh", ".py", ".yml", ".yaml",
        ".xaml", ".axaml", ".cshtml", ".razor", ".vb", ".fs", ".sql", ".props", ".targets", ".pubxml",
        ".http", ".rest", ".xml", ".config",
    };

    private readonly IVisualMcpStore _store;

    public BoardService(IVisualMcpStore store) => _store = store;

    // ── items ────────────────────────────────────────────────────────────────
    public Task<object> AddAsync(string title, string? description, string? status, string? priority,
        string[]? labels, string? assignee, string[]? steps = null)
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
                Description = ComposeDescription(description, steps),
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
                stepCount = steps?.Length ?? 0,
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

    public Task<object> MoveAsync(int id, string status, int? position, string? note = null, string? author = null)
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

            // The definition of done is recorded, not implied: closing a card carries its evidence.
            if (!string.IsNullOrWhiteSpace(note))
            {
                context.BoardComments.Add(new BoardCommentRow
                {
                    ItemId = id,
                    Author = author ?? "agent",
                    Body = $"moved to {column}: {note.Trim()}",
                    CreatedAt = now,
                });
            }

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
                note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
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

    public Task<object> BriefAsync(int staleDays) => With(context =>
    {
        var now = DateTimeOffset.UtcNow;
        var window = staleDays < 1 ? 14 : staleDays;

        var all = context.BoardItems.AsNoTracking().ToList();
        var comments = CommentCounts(context, all.Select(item => item.Id).ToList());
        var totals = Statuses.ToDictionary(status => status, status => all.Count(item => item.Status == status));
        var open = all.Where(item => item.Status != "done").ToList();

        // The card to pick up: work already started first, then what is waiting for a verdict, then the
        // queue — and inside a column the highest priority, then the board's own order.
        var ordered = open
            .OrderBy(item => StatusRank(item.Status))
            .ThenBy(item => PriorityRank(item.Priority))
            .ThenBy(item => item.Position)
            .ThenBy(item => item.Id)
            .ToList();

        var next = ordered.Count == 0 ? null : One(ordered[0], comments, now, window);
        var nextId = ordered.Count == 0 ? (int?)null : ordered[0].Id;

        var attention = open
            .Select(item => new { item, idle = Days(item.UpdatedAt, now), comments = comments.GetValueOrDefault(item.Id) })
            .Where(row => row.idle > window || (row.item.Status == "review" && row.idle > Math.Max(3, window / 2)))
            .OrderByDescending(row => row.idle)
            .Select(row => (object)new
            {
                id = row.item.Id,
                title = row.item.Title,
                status = row.item.Status,
                idleDays = row.idle,
                commentCount = row.comments,
                reason = row.item.Status == "review"
                    ? $"in review for {row.idle} days: verify it and close it, or send it back"
                    : $"untouched for {row.idle} days: still real, or already shipped? board_audit says which",
            })
            .ToList();

        // Durable notes ride along: the pinned ones in full enough to be useful, the rest by count.
        var memories = context.BoardMemories.AsNoTracking().Where(memory => memory.SupersededBy == null).ToList();
        object? memory = memories.Count == 0
            ? null
            : new
            {
                count = memories.Count,
                pinned = memories.Count(entry => entry.Pinned),
                recent = memories
                    .OrderByDescending(entry => entry.Pinned)
                    .ThenByDescending(entry => entry.UpdatedAt)
                    .Take(5)
                    .Select(entry => new
                    {
                        topic = entry.Topic,
                        kind = entry.Kind,
                        pinned = entry.Pinned,
                        body = Clip(entry.Body, 240),
                    })
                    .ToList(),
                hint = "board_recall{query} reads the rest; board_remember{topic, body, kind} writes one.",
            };

        return (object)new
        {
            source = "The board is the source of truth for what is missing and what comes next. " +
                     "Memory notes, docs/gaps.md and any backlog file are historical records: do not read them to answer this.",
            totals,
            openCount = open.Count,
            itemCount = all.Count,
            doneShare = all.Count == 0 ? (double?)null : Math.Round(100.0 * totals["done"] / all.Count, 1),
            next,
            open = ordered.Select(item => One(item, comments, now, window)).ToList(),
            attention,
            memory,
            staleAfterDays = window,
            nextCall = nextId is null
                ? "Nothing is open. Add a card with board_add, or ask the user what to do next."
                : $"board_show{{{nextId}}} for the card itself; board_audit to see which open cards this build already ships.",
        };
    });

    public Task<BoardSummary?> SummaryAsync() => Task.FromResult(TryRead(context =>
    {
        var now = DateTimeOffset.UtcNow;
        var all = context.BoardItems.AsNoTracking().ToList();
        var memories = context.BoardMemories.AsNoTracking()
            .Where(memory => memory.SupersededBy == null)
            .ToList();

        // A board that holds only notes is still a board: the pinned ones must reach the docs read.
        if (all.Count == 0 && memories.Count == 0) return null;

        var open = all.Where(item => item.Status != "done").ToList();
        var next = open
            .OrderBy(item => StatusRank(item.Status))
            .ThenBy(item => PriorityRank(item.Priority))
            .ThenBy(item => item.Position)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        var hint = open.Count == 0
            ? memories.Count == 0
                ? "Nothing is open on the board."
                : "Nothing is open on the board; the pinned memory is attached."
            : "board_brief is the full answer to what is missing / what comes next. The board is the source of truth " +
              "for the plan — not these documents and not memory notes.";

        return new BoardSummary(
            Statuses.ToDictionary(status => status, status => all.Count(item => item.Status == status)),
            open.Count,
            all.Count,
            next is null ? null : new BoardSummaryNext(next.Id, next.Title, next.Status, next.Priority, Days(next.UpdatedAt, now)),
            hint,
            memories.Count,
            memories
                .Where(memory => memory.Pinned)
                .OrderByDescending(memory => memory.UpdatedAt)
                .Select(memory => memory.Topic)
                .Take(5)
                .ToList());
    }));

    public Task<object> AuditAsync(IReadOnlyCollection<string> knownTools, bool close, string? author) => With(context =>
    {
        var now = DateTimeOffset.UtcNow;
        var root = SolutionRoot();
        var known = new HashSet<string>(knownTools ?? [], StringComparer.OrdinalIgnoreCase);

        var open = context.BoardItems
            .AsNoTracking()
            .Where(item => item.Status != "done")
            .ToList()
            .OrderBy(item => StatusRank(item.Status))
            .ThenBy(item => item.Position)
            .ThenBy(item => item.Id)
            .ToList();

        var ids = open.Select(item => item.Id).ToList();
        var comments = context.BoardComments
            .AsNoTracking()
            .Where(comment => ids.Contains(comment.ItemId))
            .ToList()
            .GroupBy(comment => comment.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var checkedCards = new List<object>();
        var candidates = new List<BoardItemRow>();
        var evidenceById = new Dictionary<int, string>();

        foreach (var item in open)
        {
            var text = string.Join('\n', new[] { item.Title, item.Description ?? string.Empty }
                .Concat((comments.TryGetValue(item.Id, out var list) ? list : []).Select(comment => comment.Body)));

            var names = SnakeCase.Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shipped = names.Where(known.Contains).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            var absent = names.Where(name => !known.Contains(name) && !NotAName.Contains(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            var paths = FilePath.Matches(text)
                .Select(match => match.Groups[1].Value)
                .Where(path => CheckableExtensions.Contains(Path.GetExtension(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var missingPaths = paths.Where(path => !PathExists(root, path)).ToList();

            // A card can name a few things that are not tool names at all (an .editorconfig key, a table,
            // a renamed capability): one such name per four shipped tools is tolerated, beyond that the
            // card is asking for something that is genuinely absent.
            var tolerance = Math.Max(1, shipped.Count / 4);
            var verdict = shipped.Count >= 2 && missingPaths.Count == 0 && absent.Count <= tolerance
                ? "closure-candidate"
                : shipped.Count > 0
                    ? "partly-shipped"
                    : absent.Count > 0 || missingPaths.Count > 0
                        ? "open"
                        : "no-signal";

            var evidence = verdict switch
            {
                "closure-candidate" => absent.Count == 0
                    ? $"every name this card proposes exists in this build: {string.Join(", ", shipped)}"
                    : $"{shipped.Count} named tool(s) exist ({string.Join(", ", shipped)}); " +
                      $"the {absent.Count} remaining name(s) are not in the catalogue ({string.Join(", ", absent)}) — read the card before closing it",
                "partly-shipped" =>
                    $"{shipped.Count} named tool(s) exist ({string.Join(", ", shipped)}); " +
                    $"{absent.Count} name(s) do not ({string.Join(", ", absent)}), and {missingPaths.Count} path(s) are missing",
                "open" => "nothing it asks for was found in this build",
                _ => "the card names no tool and no file: check it by hand",
            };

            evidenceById[item.Id] = evidence;
            if (verdict == "closure-candidate") candidates.Add(item);

            checkedCards.Add(new
            {
                id = item.Id,
                title = item.Title,
                // Report the state the caller will find after a close, not the one before it.
                status = close && verdict == "closure-candidate" ? "done" : item.Status,
                verdict,
                shippedTools = shipped,
                absentNames = absent,
                checkedPaths = paths,
                missingPaths,
                evidence,
            });
        }

        var closed = new List<object>();
        if (close && candidates.Count > 0)
        {
            using var transaction = context.Database.BeginTransaction();
            foreach (var item in candidates)
            {
                var from = item.Status;
                var evidence = evidenceById[item.Id];

                var stored = context.BoardItems.First(row => row.Id == item.Id);
                stored.Status = "done";
                stored.Position = NextPosition(context, "done");
                stored.UpdatedAt = now;

                Record(context, item.Id, "status", from, "done", now);
                context.BoardComments.Add(new BoardCommentRow
                {
                    ItemId = item.Id,
                    Author = author ?? "board_audit",
                    Body = $"closed by board_audit: {evidence}",
                    CreatedAt = now,
                });

                closed.Add(new { id = item.Id, title = item.Title, from, evidence });
            }

            context.SaveChanges();
            Renumber(context, "done");
            context.SaveChanges();
            transaction.Commit();
        }

        return (object)new
        {
            checkedCount = checkedCards.Count,
            closed = closed.Count,
            candidates = candidates.Count,
            note = close
                ? candidates.Count == 0
                    ? "Nothing was written: no open card is a closure candidate."
                    : "The closure candidates were moved to done, each with the evidence as a comment."
                : "Nothing was written. 'closure-candidate' means every name the card proposes exists in this build — " +
                  "read it with board_show, then close it with board_move{status: done, note: ...} or re-run " +
                  "board_audit{close: true}. 'partly-shipped' lists what is here and what is not.",
            items = checkedCards,
            closedItems = closed,
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

    // ── memory ───────────────────────────────────────────────────────────────

    /// <summary>How much of a body a list answer carries; a single topic is clipped the same way on purpose.</summary>
    private const int MemoryBodyLimit = 4000;

    private static readonly string[] MemoryKinds = ["fact", "decision", "constraint", "preference", "reference"];

    public Task<BoardMemoryResult> RememberAsync(string topic, string body, string? kind, string[]? labels,
        bool pinned, string? source)
    {
        var slug = NormalizeTopic(topic);
        if (slug is null)
            return Task.FromResult(BoardMemoryResult.Fail(
                topic ?? string.Empty, "A topic is required: letters, digits, '-', '.', '_' (e.g. 'sqlite-ef-linq')."));
        if (string.IsNullOrWhiteSpace(body))
            return Task.FromResult(BoardMemoryResult.Fail(slug, "The memory body is required."));

        var type = NormalizeKind(kind);
        if (type is null) return Task.FromResult(BoardMemoryResult.Fail(slug, KindError(kind)));

        var text = body.Trim();
        var origin = string.IsNullOrWhiteSpace(source) ? null : source.Trim();

        return WithTyped(context =>
        {
            var now = DateTimeOffset.UtcNow;
            var current = CurrentMemory(context, slug);

            // The same note is not a new version, whoever writes it and from whichever store: that is what makes
            // the import idempotent and a fact stated twice a no-op instead of a duplicate topic. The kind,
            // the labels and the pin are compared too, so a note whose metadata changed is a new version.
            if (current is not null
                && string.Equals(current.Body, text, StringComparison.Ordinal)
                && string.Equals(current.Kind, type, StringComparison.Ordinal)
                && current.Pinned == pinned
                && string.Equals(current.Labels ?? string.Empty, Join(labels) ?? string.Empty, StringComparison.Ordinal))
            {
                return new BoardMemoryResult(false, true, current.Id, slug, current.Kind, false, null);
            }

            var row = new BoardMemoryRow
            {
                Topic = slug,
                Body = text,
                Kind = type,
                Labels = Join(labels),
                Pinned = pinned,
                Source = origin,
                CreatedAt = now,
                UpdatedAt = now,
            };

            context.BoardMemories.Add(row);
            context.SaveChanges();

            // The previous version keeps its text and points at its successor: a reversed decision stays readable.
            if (current is not null)
            {
                current.SupersededBy = row.Id;
                current.UpdatedAt = now;
                context.SaveChanges();
            }

            return new BoardMemoryResult(true, false, row.Id, slug, type, current is not null, current?.Id);
        }, error => BoardMemoryResult.Fail(slug, error));
    }

    public Task<object> RecallAsync(string? topic, string? query, string? kind, string? label, bool pinnedOnly, int limit)
    {
        string? slug = null;
        if (!string.IsNullOrWhiteSpace(topic))
        {
            slug = NormalizeTopic(topic);
            if (slug is null) return Fail("A topic is letters, digits, '-', '.', '_'.");
        }

        string? type = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            type = NormalizeKind(kind);
            if (type is null) return Fail(KindError(kind));
        }

        var take = limit <= 0 ? 20 : Math.Min(limit, 200);
        var needle = string.IsNullOrWhiteSpace(query) ? null : $"%{query.Trim()}%";
        var wantedLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

        return With(context =>
        {
            var rows = context.BoardMemories
                .AsNoTracking()
                .Where(memory => memory.SupersededBy == null);

            if (slug is not null) rows = rows.Where(memory => memory.Topic == slug);
            if (type is not null) rows = rows.Where(memory => memory.Kind == type);
            if (pinnedOnly) rows = rows.Where(memory => memory.Pinned);
            if (needle is not null)
            {
                rows = rows.Where(memory =>
                    EF.Functions.Like(memory.Topic, needle) || EF.Functions.Like(memory.Body, needle));
            }

            // EF Core on SQLite refuses DateTimeOffset in ORDER BY; memories are few, so the order is here.
            var current = rows
                .ToList()
                .Where(memory => wantedLabel is null ||
                                 Split(memory.Labels).Contains(wantedLabel, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(memory => memory.Pinned)
                .ThenByDescending(memory => memory.UpdatedAt)
                .ThenByDescending(memory => memory.Id)
                .ToList();

            var items = current.Take(take).Select(memory => new
            {
                id = memory.Id,
                topic = memory.Topic,
                kind = memory.Kind,
                labels = Split(memory.Labels),
                pinned = memory.Pinned,
                source = memory.Source,
                chars = memory.Body.Length,
                truncated = memory.Body.Length > MemoryBodyLimit,
                updatedAt = memory.UpdatedAt.ToUniversalTime().ToString("O"),
                body = Clip(memory.Body, MemoryBodyLimit),
            }).ToList();

            return (object)new
            {
                count = items.Count,
                total = current.Count,
                topic = slug,
                query,
                items,
                hint = current.Count == 0
                    ? "No memory matches. Write one with board_remember{topic, body, kind} — a fact, a decision, a constraint — not in a file or a chat message."
                    : "board_brief carries the pinned ones; board_forget{topic} retires a topic.",
            };
        });
    }

    public Task<object> ForgetAsync(string topic)
    {
        var slug = NormalizeTopic(topic);
        if (slug is null) return Fail("A topic is letters, digits, '-', '.', '_'.");

        return With(context =>
        {
            var now = DateTimeOffset.UtcNow;
            var current = context.BoardMemories
                .Where(memory => memory.Topic == slug && memory.SupersededBy == null)
                .ToList();

            if (current.Count == 0)
                return (object)new { forgotten = false, topic = slug, note = "No current memory with this topic." };

            foreach (var row in current)
            {
                row.SupersededBy = -1; /* no successor: history, never current again */
                row.UpdatedAt = now;
            }

            context.SaveChanges();
            return new
            {
                forgotten = true,
                topic = slug,
                versions = current.Count,
                note = "The versions stay in board_memory as history and are not returned again.",
            };
        });
    }

    public Task<object> TickPlanAsync(int id, int[]? done, int[]? notDone)
    {
        if (id < 1) return Fail("A valid item id is required.");

        return With(context =>
        {
            var item = context.BoardItems.FirstOrDefault(row => row.Id == id);
            if (item is null) return (object)new { error = $"No board item with id {id}." };

            var lines = (item.Description ?? string.Empty).Split('\n');
            if (!lines.Any(IsChecklistLine))
            {
                return new
                {
                    error = $"Card {id} has no checklist. Create one with create_task_checklist{{title, steps}} " +
                            "or board_add{title, steps: [...]}.",
                };
            }

            var doneSet = new HashSet<int>(done ?? []);
            var notDoneSet = new HashSet<int>(notDone ?? []);
            var step = 0;
            var updated = 0;
            var doneCount = 0;

            for (var index = 0; index < lines.Length; index++)
            {
                if (!IsChecklistLine(lines[index])) continue;

                step++;
                var target = doneSet.Contains(step) ? true : notDoneSet.Contains(step) ? false : (bool?)null;
                if (target is not null)
                {
                    lines[index] = SetCheckbox(lines[index], target.Value);
                    updated++;
                }

                if (IsChecked(lines[index])) doneCount++;
            }

            item.Description = string.Join('\n', lines);
            item.UpdatedAt = DateTimeOffset.UtcNow;
            context.SaveChanges();

            return new
            {
                ticked = true,
                cardId = id,
                updated,
                totalSteps = step,
                doneCount,
                remaining = step - doneCount,
                allDone = step > 0 && doneCount == step,
                content = item.Description,
            };
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

    /// <summary>One card as the brief shows it: identity, age and signals — never the whole description.</summary>
    private static object One(BoardItemRow item, Dictionary<int, int> comments, DateTimeOffset now, int staleDays) => new
    {
        id = item.Id,
        title = item.Title,
        status = item.Status,
        priority = item.Priority,
        labels = Split(item.Labels),
        assignee = item.Assignee,
        ageDays = Days(item.CreatedAt, now),
        idleDays = Days(item.UpdatedAt, now),
        stale = Days(item.UpdatedAt, now) > staleDays,
        commentCount = comments.GetValueOrDefault(item.Id),
    };

    private static Dictionary<int, int> CommentCounts(VisualMcpDbContext context, List<int> ids)
    {
        if (ids.Count == 0) return [];

        return context.BoardComments
            .AsNoTracking()
            .Where(comment => ids.Contains(comment.ItemId))
            .GroupBy(comment => comment.ItemId)
            .Select(group => new { ItemId = group.Key, Count = group.Count() })
            .ToDictionary(row => row.ItemId, row => row.Count);
    }

    /// <summary>The order a person would pick work up in, from a column's point of view.</summary>
    private static int StatusRank(string status) => status switch
    {
        "in_progress" => 0,
        "review" => 1,
        "todo" => 2,
        "backlog" => 3,
        _ => 4,
    };

    private static int PriorityRank(string priority) => priority switch
    {
        "highest" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4,
    };

    private static int Days(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(0, (int)Math.Floor((to - from).TotalDays));

    /// <summary>The solution folder: the parent of the store's .visualMCP directory.</summary>
    private string? SolutionRoot()
    {
        var database = _store.DatabasePath;
        if (string.IsNullOrWhiteSpace(database)) return null;

        var store = Path.GetDirectoryName(database);
        return store is null ? null : Path.GetDirectoryName(store);
    }

    private static bool PathExists(string? root, string path)
    {
        if (root is null) return true; /* no root to check against: never report a path as missing */
        var candidate = Path.IsPathRooted(path) ? path : Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar));
        return File.Exists(candidate) || Directory.Exists(candidate);
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

    /// <summary>Like <see cref="With"/>, for a typed answer: a database failure is turned into <paramref name="failure"/>.</summary>
    private Task<T> WithTyped<T>(Func<VisualMcpDbContext, T> work, Func<string, T> failure)
    {
        var path = _store.EnsureDatabase();
        if (path is null)
        {
            return Task.FromResult(failure(
                "No solution is loaded (or its folder is not writable), so there is no board database. " +
                "Call load_solution first."));
        }

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
            return Task.FromResult(failure($"Board database error: {ex.Message}"));
        }
    }

    /// <summary>Like <see cref="With"/>, but an unavailable or empty board is null instead of an error object.</summary>
    private BoardSummary? TryRead(Func<VisualMcpDbContext, BoardSummary?> work)
    {
        var path = _store.EnsureDatabase();
        if (path is null) return null;

        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            VisualMcpSchema.Ensure(connection);

            using var context = VisualMcpSchema.CreateContext(connection);
            return work(context);
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        {
            return null;
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

    // ── memory and checklist helpers ─────────────────────────────────────────

    /// <summary>A topic is a slug: lowercase, letters/digits, and '-', '.', '_' between them.</summary>
    private static string? NormalizeTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;

        var chars = topic.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '.' or '_' ? character : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-', '.', '_');
        return slug.Length == 0 ? null : slug;
    }

    private static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return "fact";
        var candidate = kind.Trim().ToLowerInvariant();
        return MemoryKinds.FirstOrDefault(value => value == candidate);
    }

    private static string KindError(string? kind) =>
        $"'{kind}' is not a memory kind. Use one of: {string.Join(", ", MemoryKinds)}.";

    /// <summary>The one row of a topic that has no successor: the version that counts.</summary>
    private static BoardMemoryRow? CurrentMemory(VisualMcpDbContext context, string topic) =>
        context.BoardMemories
            .Where(memory => memory.Topic == topic && memory.SupersededBy == null)
            .OrderByDescending(memory => memory.Id)
            .FirstOrDefault();

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit];

    /// <summary>
    /// The card body: the description as given, then the steps as a GFM checklist. This is what makes a plan a
    /// card instead of a file, and what <see cref="TickPlanAsync"/> ticks.
    /// </summary>
    private static string? ComposeDescription(string? description, string[]? steps)
    {
        var plan = steps is { Length: > 0 }
            ? string.Join('\n', steps
                .Where(step => !string.IsNullOrWhiteSpace(step))
                .Select(step => "- [ ] " + step.Trim()))
            : null;

        if (string.IsNullOrWhiteSpace(description)) return plan;
        return plan is null ? description : description.TrimEnd() + "\n\n" + plan;
    }

    private static bool IsChecklistLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("- [ ] ", StringComparison.Ordinal)
            || trimmed.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChecked(string line) =>
        line.TrimStart().StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase);

    private static string SetCheckbox(string line, bool isDone)
    {
        var start = line.IndexOf('[', StringComparison.Ordinal);
        var end = line.IndexOf(']', StringComparison.Ordinal);
        if (start < 0 || end <= start) return line;
        return line[..start] + (isDone ? "[x]" : "[ ]") + line[(end + 1)..];
    }
}
