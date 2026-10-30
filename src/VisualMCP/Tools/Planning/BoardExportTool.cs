using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Planning;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class BoardExportTool
{
    [McpServerTool(Name = "board_export"), Description(
        "The board as Markdown: one '## column' section per column, each card as a bullet with its id, priority, " +
        "assignee, labels and description. Use it to put the plan into a document, a PR description or a handoff, " +
        "or to feed a written backlog back through board_import. Read-only.")]
    public static Task<object> BoardExport(
        IBoardService board,
        [Description("Only this column: backlog, todo, in_progress, review, done. Omit for the whole board.")] string? status = null)
        => board.ExportAsync(status);
}
