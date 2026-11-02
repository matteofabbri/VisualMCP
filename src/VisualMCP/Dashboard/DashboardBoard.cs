using System.Text.Json;
using System.Text.Json.Nodes;
using Snitch.Planning;
using Snitch.Storage;

namespace VisualMCP.Dashboard;

/// <summary>One card as the board page draws it: the same fields the board tools return.</summary>
public sealed record BoardCardView(
    int Id,
    string Title,
    string? Description,
    string Status,
    string Priority,
    IReadOnlyList<string> Labels,
    string? Assignee,
    int Position,
    string? CreatedAt,
    string? UpdatedAt,
    int CommentCount);

/// <summary>One column of the board, with its cards in position order.</summary>
public sealed record BoardColumnView(string Status, int Count, IReadOnlyList<BoardCardView> Cards);

/// <summary>One comment on a card, oldest first when listed.</summary>
public sealed record BoardCommentView(int Id, string? Author, string Body, string CreatedAt);

/// <summary>One recorded field change of a card.</summary>
public sealed record BoardHistoryView(string Field, string? From, string? To, string At);

/// <summary>A card with everything the detail panel shows: the fields, the comments and the history.</summary>
public sealed record BoardDetailView(
    BoardCardView Card,
    IReadOnlyList<BoardCommentView> Comments,
    IReadOnlyList<BoardHistoryView> History);

/// <summary>The outcome of one board write: the card id when a card was created, the error otherwise.</summary>
public sealed record BoardWriteResult(bool Ok, int? Id, string? Error)
{
    public static BoardWriteResult Success(int? id = null) => new(true, id, null);
    public static BoardWriteResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// The board as the web interface sees it: the same <see cref="IBoardService"/> the <c>board_*</c>
/// tools call, projected into the typed records the page renders. Read paths map the service's
/// documents; write paths validate the input here, call the service, and surface its own error text —
/// so a card moved from the browser is moved by exactly the code a tool call uses (history,
/// renumbering and all), and the two can never drift apart.
/// </summary>
public sealed class DashboardBoard
{
    /// <summary>The columns, left to right: the board service's own contract.</summary>
    public static readonly string[] Columns = BoardService.Statuses;

    /// <summary>The priorities the board accepts, lowest first.</summary>
    public static readonly string[] Priorities = ["lowest", "low", "medium", "high", "highest"];

    private readonly IBoardService _board;

    public DashboardBoard(IVisualMcpStore store) => _board = new BoardService(store);

    /// <summary>Every column with its cards. A store that is not there yet is five empty columns.</summary>
    public async Task<IReadOnlyList<BoardColumnView>> SnapshotAsync()
    {
        var node = Node(await _board.BoardAsync());
        if (node?["columns"] is not JsonArray columns)
            return Columns.Select(status => new BoardColumnView(status, 0, [])).ToList();

        var result = new List<BoardColumnView>(columns.Count);
        foreach (var column in columns)
        {
            var status = Str(column?["status"]) ?? "backlog";
            var cards = column?["items"] is JsonArray items
                ? items.Select(Card).OfType<BoardCardView>().ToList()
                : [];
            result.Add(new BoardColumnView(status, Int(column?["count"]) ?? cards.Count, cards));
        }

        return result;
    }

    /// <summary>One card with its comments and history, or null when the id names no card.</summary>
    public async Task<BoardDetailView?> DetailAsync(int id)
    {
        if (id < 1) return null;

        var node = Node(await _board.ShowAsync(id));
        if (node?["error"] is not null) return null;

        var card = Card(node?["item"]);
        if (card is null) return null;

        var comments = node?["comments"] is JsonArray rows
            ? rows.Select(comment => new BoardCommentView(
                    Int(comment?["id"]) ?? 0,
                    Str(comment?["author"]),
                    Str(comment?["body"]) ?? string.Empty,
                    Str(comment?["createdAt"]) ?? string.Empty))
                .ToList()
            : [];

        var history = node?["history"] is JsonArray entries
            ? entries.Select(entry => new BoardHistoryView(
                    Str(entry?["field"]) ?? string.Empty,
                    Str(entry?["from"]),
                    Str(entry?["to"]),
                    Str(entry?["at"]) ?? string.Empty))
                .ToList()
            : [];

        return new BoardDetailView(card, comments, history);
    }

    public async Task<BoardWriteResult> AddAsync(string title, string? description, string? status,
        string? priority, string? assignee, string? labels)
    {
        if (string.IsNullOrWhiteSpace(title)) return BoardWriteResult.Fail("A title is required.");

        var column = Normalize(status, Columns);
        if (column is null) return BoardWriteResult.Fail(StatusError(status));

        var level = Normalize(priority, Priorities);
        if (level is null) return BoardWriteResult.Fail(PriorityError(priority));

        var node = Node(await _board.AddAsync(title, description, column, level, Split(labels), assignee));
        return Error(node) ?? BoardWriteResult.Success(Int(node?["id"]));
    }

    public async Task<BoardWriteResult> UpdateAsync(int id, string? title, string? description,
        string? priority, string? assignee, string? labels)
    {
        if (id < 1) return BoardWriteResult.Fail("A valid card id is required.");
        if (title is not null && string.IsNullOrWhiteSpace(title))
            return BoardWriteResult.Fail("A card title cannot be empty.");

        var level = priority is null ? null : Normalize(priority, Priorities);
        if (priority is not null && level is null) return BoardWriteResult.Fail(PriorityError(priority));

        // null labels means "not in this request"; an empty set is a request to clear them.
        var parsedLabels = labels is null ? null : Split(labels);

        var node = Node(await _board.UpdateAsync(id, title, description, level, parsedLabels, assignee));
        return Error(node) ?? BoardWriteResult.Success(id);
    }

    public async Task<BoardWriteResult> MoveAsync(int id, string? status, int? position)
    {
        if (id < 1) return BoardWriteResult.Fail("A valid card id is required.");

        var column = Normalize(status, Columns);
        if (column is null) return BoardWriteResult.Fail(StatusError(status));
        if (position is < 0) return BoardWriteResult.Fail("A position cannot be negative.");

        var node = Node(await _board.MoveAsync(id, column, position));
        return Error(node) ?? BoardWriteResult.Success(id);
    }

    public async Task<BoardWriteResult> CommentAsync(int id, string? body, string? author)
    {
        if (id < 1) return BoardWriteResult.Fail("A valid card id is required.");
        if (string.IsNullOrWhiteSpace(body)) return BoardWriteResult.Fail("A comment body is required.");

        var node = Node(await _board.CommentAsync(id, body, author));
        return Error(node) ?? BoardWriteResult.Success(id);
    }

    public async Task<BoardWriteResult> DeleteAsync(int id)
    {
        if (id < 1) return BoardWriteResult.Fail("A valid card id is required.");

        var node = Node(await _board.DeleteAsync(id));
        return Error(node) ?? BoardWriteResult.Success(id);
    }

    // ── the service documents, read as nodes ─────────────────────────────────

    // The board service answers its MCP tools with JSON documents; the page reads the same document
    // instead of a second projection of the tables, so a field the tools gain is one mapping away.
    private static JsonNode? Node(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value);

    private static BoardWriteResult? Error(JsonNode? node)
    {
        if (node?["error"] is null) return null;
        return BoardWriteResult.Fail(Str(node?["error"]) ?? "The board service refused the write.");
    }

    private static BoardCardView? Card(JsonNode? node)
    {
        var id = Int(node?["id"]);
        if (id is null || node is null) return null;

        return new BoardCardView(
            id.Value,
            Str(node["title"]) ?? string.Empty,
            Str(node["description"]),
            Str(node["status"]) ?? "backlog",
            Str(node["priority"]) ?? "medium",
            Strings(node["labels"]),
            Str(node["assignee"]),
            Int(node["position"]) ?? 0,
            Str(node["createdAt"]),
            Str(node["updatedAt"]),
            Int(node["commentCount"]) ?? 0);
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        return value.TryGetValue<long>(out var wide) ? (int)wide : null;
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(Str).OfType<string>().ToList()
            : [];

    private static string? Normalize(string? value, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return allowed.FirstOrDefault(item => item == candidate);
    }

    private static string[]? Split(string? labels) =>
        string.IsNullOrWhiteSpace(labels)
            ? []
            : labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string StatusError(string? status) =>
        $"'{status}' is not a column. Use one of: {string.Join(", ", Columns)}.";

    private static string PriorityError(string? priority) =>
        $"'{priority}' is not a priority. Use one of: {string.Join(", ", Priorities)}.";
}
