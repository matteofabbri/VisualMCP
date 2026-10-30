using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardListTool
{
    [McpServerTool(Name = "board_list"), Description(
        "List cards, optionally filtered by column, priority, assignee, label or free text in title/description. " +
        "Ordered by column then position. Read-only — use board_board for the grouped view.")]
    public static Task<object> BoardList(
        IBoardService board,
        [Description("Only this column: backlog, todo, in_progress, review, done.")] string? status = null,
        [Description("Only cards carrying this label.")] string? label = null,
        [Description("Only cards assigned to this person/agent.")] string? assignee = null,
        [Description("Only this priority: lowest, low, medium, high, highest.")] string? priority = null,
        [Description("Text to find in the title or description.")] string? text = null)
        => board.ListAsync(status, label, assignee, priority, text);
}
