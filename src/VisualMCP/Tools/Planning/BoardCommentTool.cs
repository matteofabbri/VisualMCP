using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardCommentTool
{
    [McpServerTool(Name = "board_comment"), Description(
        "Add a comment to a card: a progress note, a blocker, the reason a decision was taken. Comments are " +
        "append-only and stay attached to the card. Returns the comment and the card's comment count.")]
    public static Task<object> BoardComment(
        IBoardService board,
        [Description("Card id.")] int id,
        [Description("Comment text.")] string body,
        [Description("Author of the comment (agent name or person).")] string? author = null)
        => board.CommentAsync(id, body, author);
}
