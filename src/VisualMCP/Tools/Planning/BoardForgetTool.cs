using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardForgetTool
{
    [McpServerTool(Name = "board_forget"), Description(
        "Retire a topic on the board: its versions stay in the store as history but no version is current any " +
        "more, so board_recall and board_brief stop returning it. Use it when a note became wrong rather than " +
        "rewriting history — to correct a fact, call board_remember on the same topic instead, which supersedes " +
        "the previous version. Returns what was retired; writes to the board database only.")]
    public static Task<object> BoardForget(
        IBoardService board,
        [Description("Topic slug to retire, e.g. 'sqlite-ef-linq'.")] string topic)
        => board.ForgetAsync(topic);
}
