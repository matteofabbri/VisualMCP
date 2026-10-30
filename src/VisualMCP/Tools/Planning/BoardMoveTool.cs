using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardMoveTool
{
    [McpServerTool(Name = "board_move"), Description(
        "Move a card to another column, optionally at a given position (0 = top). The move is recorded in the card's " +
        "history, and the destination column is renumbered so positions stay contiguous. Returns the card.")]
    public static Task<object> BoardMove(
        IBoardService board,
        [Description("Card id.")] int id,
        [Description("Destination column: backlog, todo, in_progress, review, done.")] string status,
        [Description("Position inside the column (0 = top). Omit to append at the bottom.")] int? position = null)
        => board.MoveAsync(id, status, position);
}
