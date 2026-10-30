using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardUpdateTool
{
    [McpServerTool(Name = "board_update"), Description(
        "Edit a card's fields (title, description, priority, labels, assignee). Only the arguments you pass change; " +
        "every change is written to the card's history. To change the column use board_move. Returns the resulting card.")]
    public static Task<object> BoardUpdate(
        IBoardService board,
        [Description("Card id.")] int id,
        [Description("New title.")] string? title = null,
        [Description("New description (replaces the old one).")] string? description = null,
        [Description("New priority: lowest, low, medium, high, highest.")] string? priority = null,
        [Description("New labels (replaces the old set).")] string[]? labels = null,
        [Description("New assignee.")] string? assignee = null)
        => board.UpdateAsync(id, title, description, priority, labels, assignee);
}
