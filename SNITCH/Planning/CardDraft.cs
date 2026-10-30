namespace Snitch.Planning;

/// <summary>
/// One card as it is described outside the board — a backlog document, a plan, another agent —
/// before it becomes a row. <see cref="IBoardService.ImportAsync"/> turns these into cards keyed on
/// <see cref="Title"/>, so importing the same list twice does not duplicate anything.
/// </summary>
public sealed class CardDraft
{
    /// <summary>The card title; it is also the import key. Required.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>What has to be done, in full: context, acceptance criteria, links.</summary>
    public string? Description { get; set; }

    /// <summary>Column: backlog, todo, in_progress, review, done. Defaults to backlog.</summary>
    public string? Status { get; set; }

    /// <summary>Priority: lowest, low, medium, high, highest. Defaults to medium.</summary>
    public string? Priority { get; set; }

    /// <summary>Free-form labels.</summary>
    public string[]? Labels { get; set; }

    /// <summary>Who owns the card.</summary>
    public string? Assignee { get; set; }
}
