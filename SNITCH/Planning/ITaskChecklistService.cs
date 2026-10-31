namespace Snitch.Planning;

/// <summary>
/// A multi-step plan, stored as a board card: the steps are a GFM checklist in the card's description, so the
/// plan shows up in <c>board_brief</c>, <c>board_show</c> and <c>board_export</c> like every other card and no
/// file is written. A step that becomes real work is promoted with <c>board_add</c>.
/// </summary>
public interface ITaskChecklistService
{
    /// <summary>Create the plan card; <paramref name="status"/> defaults to the board's first column.</summary>
    Task<object> CreateAsync(string title, string[] steps, string? status, string? priority, string[]? labels);

    /// <summary>Tick steps off (1-based positions) in the card's own checklist.</summary>
    Task<object> UpdateAsync(int id, int[]? done, int[]? notDone);
}
