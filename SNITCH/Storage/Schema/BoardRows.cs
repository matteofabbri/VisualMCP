namespace Snitch.Storage.Schema;

/// <summary>One card of the kanban board.</summary>
public sealed class BoardItemRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string? Labels { get; set; }
    public string? Assignee { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A comment on a card; append-only.</summary>
public sealed class BoardCommentRow
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string? Author { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One version of a durable note: a fact, a decision, a constraint. A topic has exactly one current row
/// (the one with no successor); an update inserts a new row and points the old one at it, so a reversed
/// decision stays readable instead of being overwritten. <see cref="SupersededBy"/> = -1 marks a forgotten
/// topic, which keeps its history but is no longer returned.
/// </summary>
public sealed class BoardMemoryRow
{
    public int Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Kind { get; set; } = "fact";
    public string? Labels { get; set; }
    public bool Pinned { get; set; }
    public string? Source { get; set; }
    public int? SupersededBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One field change of a card, so its state is never a guess.</summary>
public sealed class BoardHistoryRow
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset At { get; set; }
}
