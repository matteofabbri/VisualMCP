using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardMoveTool
{
    [McpServerTool(Name = "board_move"), Description(
        "Move a card to another column, optionally at a given position (0 = top), or move several cards to the same " +
        "column in one call with ids — closing a batch of finished cards is one call, not one per card. The move is " +
        "recorded in each card's history, and the columns involved are renumbered so positions stay contiguous. " +
        "Pass note= when closing: the definition of done is then recorded as a comment on every card moved (what " +
        "shipped, which test proved it) instead of being implied. Returns the card for a single move, a summary " +
        "for a batch.")]
    public static Task<object> BoardMove(
        IBoardService board,
        [Description("Destination column: backlog, todo, in_progress, review, done.")] string status,
        [Description("Card id, for a single move.")] int id = 0,
        [Description("Several card ids moved to the same column in one transaction; wins over id.")] int[]? ids = null,
        [Description("Position inside the column (0 = top), single moves only. Omit to append at the bottom.")] int? position = null,
        [Description("Optional note recorded as a comment on the card — the evidence for the move, especially when closing it.")] string? note = null,
        [Description("Author of the note (agent name or person).")] string? author = null)
        => ids is { Length: > 0 }
            ? board.MoveManyAsync(ids, status, note, author)
            : board.MoveAsync(id, status, position, note, author);
}
