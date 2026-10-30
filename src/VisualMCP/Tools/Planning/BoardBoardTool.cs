using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardBoardTool
{
    [McpServerTool(Name = "board_board"), Description(
        "The whole board as columns of cards, with per-column counts, total cards and the share that is done. " +
        "This is the overview to read at the start of a session. Read-only.")]
    public static Task<object> BoardBoard(IBoardService board) => board.BoardAsync();
}
