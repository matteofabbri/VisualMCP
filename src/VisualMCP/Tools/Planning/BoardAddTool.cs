using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardAddTool
{
    [McpServerTool(Name = "board_add"), Description(
        "Create a card on the project's board (Jira-style, stored in the solution's SQLite database) — the durable " +
        "plan that survives sessions; this IS the checklist, a file is not. Columns: backlog → todo → in_progress → " +
        "review → done. Priorities: lowest, low, medium, high, highest. Pass steps to put a checklist in the card's " +
        "description (tick it with update_task_checklist). Name the tools, files and symbols the card is about in its " +
        "description: board_audit uses those names to prove the card is already done, and board_brief reports the " +
        "cards that have gone stale.")]
    public static Task<object> BoardAdd(
        IBoardService board,
        [Description("Card title (short, imperative).")] string title,
        [Description("What has to be done, in full: context, acceptance criteria, links.")] string? description = null,
        [Description("Column: backlog (default), todo, in_progress, review, done.")] string? status = null,
        [Description("Priority: lowest, low, medium (default), high, highest.")] string? priority = null,
        [Description("Free-form labels, e.g. [\"docker\", \"api\"].")] string[]? labels = null,
        [Description("Who owns the card (an agent name or person).")] string? assignee = null,
        [Description("Optional ordered steps: they become a checklist in the card's description (tick with update_task_checklist{id, done}).")] string[]? steps = null)
        => board.AddAsync(title, description, status, priority, labels, assignee, steps);
}
