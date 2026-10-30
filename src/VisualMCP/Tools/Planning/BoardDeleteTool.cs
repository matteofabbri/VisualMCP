using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardDeleteTool
{
    [McpServerTool(Name = "board_delete"), Description(
        "Delete a card together with its comments and history. Irreversible. Returns the card as it was.")]
    public static Task<object> BoardDelete(
        IBoardService board,
        [Description("Card id.")] int id) => board.DeleteAsync(id);
}
