using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardBriefTool
{
    [McpServerTool(Name = "board_brief"), Description(
        "THE ANSWER to \"what is missing / what do we do next\", in one compact call: per-column counts, the card to " +
        "pick up (work already in progress first, then what waits for a verdict, then the queue — highest priority " +
        "first), every open card with its age, its idle days and a stale flag, and the cards that need a look. START " +
        "HERE instead of reading docs/gaps.md, a backlog file or memory notes: the board is the source of truth for " +
        "the plan and those are historical records. Read-only.")]
    public static Task<object> BoardBrief(
        IBoardService board,
        [Description("A card untouched for more than this many days is flagged stale and listed under attention (default 14).")]
        int staleDays = 14)
        => board.BriefAsync(staleDays);
}
