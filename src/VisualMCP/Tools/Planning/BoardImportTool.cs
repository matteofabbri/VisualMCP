using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardImportTool
{
    [McpServerTool(Name = "board_import"), Description(
        "Create MANY cards in one call from a written backlog — a gap inventory, a plan, another agent's list. " +
        "Cards are keyed on the title, so importing the same list again does not duplicate anything: 'existing' " +
        "decides what happens to a title already on the board (skip, default; update its fields; duplicate anyway). " +
        "Each card is {title, description, status, priority, labels, assignee}; a bad card is reported in 'errors' " +
        "and the rest still import. This is a one-way bootstrap: the document is the input, and once imported the " +
        "BOARD is what the plan is kept in (use board_export for a snapshot of it). Source documents are not modified.")]
    public static Task<object> BoardImport(
        IBoardService board,
        [Description("The cards to create, as an array of objects: {title, description, status, priority, labels, assignee}. " +
                     "Example: [{\"title\":\"Fix the leak\",\"status\":\"todo\",\"priority\":\"high\",\"labels\":[\"bug\"]}]")]
        CardDraft[] cards,
        [Description("What to do when a card with the same title already exists: skip (default), update (apply the " +
                     "draft's fields), duplicate (create another card with the same title).")] string? existing = null)
        => board.ImportAsync(cards, existing);
}
