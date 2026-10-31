using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardRememberTool
{
    [McpServerTool(Name = "board_remember"), Description(
        "Write a durable note onto the board — a fact, a decision, a constraint, a preference — under a topic. " +
        "One current note per topic: writing the same topic again supersedes the previous version (which stays " +
        "as history) instead of duplicating it. THIS is where an agent's memory belongs: not in MEMORY.md, not " +
        "in a *.md handoff, not in a chat message the next session never reads. Use `pinned: true` for the few " +
        "facts every session must see, because board_brief and read_project_docs carry the pinned ones; " +
        "board_recall searches the rest. Read back before writing: recall{query} first, so a topic is updated " +
        "rather than duplicated under a new name.")]
    public static async Task<object> BoardRemember(
        IBoardService board,
        [Description("Stable topic slug, e.g. 'sqlite-ef-linq' or 'hook-routing'. Reusing it updates, not duplicates.")]
        string topic,
        [Description("The note itself: one fact, written so a reader with no context can act on it.")]
        string body,
        [Description("Kind: fact (default), decision, constraint, preference or reference.")]
        string? kind = null,
        [Description("Free-form labels, e.g. [\"engine\", \"sqlite\"].")] string[]? labels = null,
        [Description("Pin it: pinned notes ride along in board_brief and in read_project_docs.")] bool pinned = false,
        [Description("Where it came from (session, agent, file) — recorded, never interpreted.")] string? source = null)
        => await board.RememberAsync(topic, body, kind, labels, pinned, source).ConfigureAwait(false);
}
