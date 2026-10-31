using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardMoveTool
{
    [McpServerTool(Name = "board_move"), Description(
        "Move a card to another column, optionally at a given position (0 = top). The move is recorded in the card's " +
        "history, and the destination column is renumbered so positions stay contiguous. Pass note= when closing a " +
        "card: the definition of done is then recorded as a comment (what shipped, which test proved it) instead of " +
        "being implied. Returns the card.")]
    public static Task<object> BoardMove(
        IBoardService board,
        [Description("Card id.")] int id,
        [Description("Destination column: backlog, todo, in_progress, review, done.")] string status,
        [Description("Position inside the column (0 = top). Omit to append at the bottom.")] int? position = null,
        [Description("Optional note recorded as a comment on the card — the evidence for the move, especially when closing it.")] string? note = null,
        [Description("Author of the note (agent name or person).")] string? author = null)
        => board.MoveAsync(id, status, position, note, author);
}
