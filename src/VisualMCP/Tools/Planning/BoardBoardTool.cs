using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardBoardTool
{
    [McpServerTool(Name = "board_board"), Description(
        "The board's canonical overview: every column with its cards, per-column counts, the total and the share done. " +
        "This board IS the project's plan and the source of truth for what is left to do — not docs/gaps.md, not a " +
        "backlog file, not a memory note; those are historical records. For the compact answer to \"what is missing / " +
        "what comes next\" (counts, the card to pick up, every open card with its age and a stale flag) use board_brief; " +
        "for one column or a filter use board_list. Read-only.")]
    public static Task<object> BoardBoard(IBoardService board) => board.BoardAsync();
}
