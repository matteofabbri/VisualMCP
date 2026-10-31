namespace Snitch.Planning;

/// <summary>
/// The checklist is a card, not a file: <see cref="CreateAsync"/> delegates to the board (which composes the
/// steps into the card's description) and <see cref="UpdateAsync"/> ticks them there. That is deliberate — a
/// plan written to a Markdown file is a plan the next session does not read, while a card is the plan the board
/// already answers with.
/// </summary>
public sealed class TaskChecklistService : ITaskChecklistService
{
    private readonly IBoardService _board;

    public TaskChecklistService(IBoardService board) => _board = board;

    public Task<object> CreateAsync(string title, string[] steps, string? status, string? priority, string[]? labels)
        => _board.AddAsync(title, null, status, priority, labels, null, steps);

    public Task<object> UpdateAsync(int id, int[]? done, int[]? notDone)
        => _board.TickPlanAsync(id, done, notDone);
}
