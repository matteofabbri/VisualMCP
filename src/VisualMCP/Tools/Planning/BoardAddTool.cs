using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardAddTool
{
    [McpServerTool(Name = "board_add"), Description(
        "Create a card on the project's internal kanban board (Jira-style, stored in the solution's SQLite database). " +
        "Columns: backlog → todo → in_progress → review → done. Priorities: lowest, low, medium, high, highest. " +
        "Use it to plan work that spans sessions: unlike the checklist, cards survive restarts and carry comments, " +
        "labels, an assignee and a full change history.")]
    public static Task<object> BoardAdd(
        IBoardService board,
        [Description("Card title (short, imperative).")] string title,
        [Description("What has to be done, in full: context, acceptance criteria, links.")] string? description = null,
        [Description("Column: backlog (default), todo, in_progress, review, done.")] string? status = null,
        [Description("Priority: lowest, low, medium (default), high, highest.")] string? priority = null,
        [Description("Free-form labels, e.g. [\"docker\", \"api\"].")] string[]? labels = null,
        [Description("Who owns the card (an agent name or person).")] string? assignee = null)
        => board.AddAsync(title, description, status, priority, labels, assignee);
}
