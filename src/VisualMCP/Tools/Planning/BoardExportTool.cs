using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardExportTool
{
    [McpServerTool(Name = "board_export"), Description(
        "The board as Markdown: one '## column' section per column, each card as a bullet with its id, priority, " +
        "assignee, labels and description. A SNAPSHOT of the board for a document, a PR description or a handoff — " +
        "the board itself stays the source of truth, so do not read the snapshot back as a backlog. Read-only.")]
    public static Task<object> BoardExport(
        IBoardService board,
        [Description("Only this column: backlog, todo, in_progress, review, done. Omit for the whole board.")] string? status = null)
        => board.ExportAsync(status);
}
