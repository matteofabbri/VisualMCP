using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardRecallTool
{
    [McpServerTool(Name = "board_recall"), Description(
        "Read the durable notes stored with the board: an exact topic, or a search over topic and body. " +
        "Call it BEFORE board_remember so a fact is updated instead of duplicated under a new topic, and when " +
        "you need what an earlier session learned — the board is the memory this server keeps, and there is no " +
        "memory_* tool. Pinned notes also arrive with board_brief and read_project_docs. Read-only.")]
    public static Task<object> BoardRecall(
        IBoardService board,
        [Description("Exact topic slug, e.g. 'sqlite-ef-linq'. Omit to search.")] string? topic = null,
        [Description("Words to look for in the topic and the body (LIKE, case-insensitive).")] string? query = null,
        [Description("Only this kind: fact, decision, constraint, preference or reference.")] string? kind = null,
        [Description("Only notes carrying this label, e.g. 'engine'.")] string? label = null,
        [Description("Only pinned notes.")] bool pinnedOnly = false,
        [Description("Maximum notes to return (default 20, max 200).")] int limit = 20)
        => board.RecallAsync(topic, query, kind, label, pinnedOnly, limit);
}
