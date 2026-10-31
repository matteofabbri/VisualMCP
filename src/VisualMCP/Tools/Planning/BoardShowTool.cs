using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardShowTool
{
    [McpServerTool(Name = "board_show"), Description(
        "One card in full: its fields, every comment in order and the complete change history (created, moves, " +
        "field edits). Read this before closing a card that board_audit could only call 'partly-shipped', and after " +
        "picking one from board_brief. Read-only.")]
    public static Task<object> BoardShow(
        IBoardService board,
        [Description("Card id.")] int id) => board.ShowAsync(id);
}
