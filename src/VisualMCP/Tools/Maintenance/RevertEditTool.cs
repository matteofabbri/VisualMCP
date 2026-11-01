using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Storage;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class RevertEditTool
{
    [McpServerTool(Name = "revert_edit"), Description(
        "Undo one write this server made, by id from edit_history. The previous file text is kept compressed in " +
        ".visualMCP/visualmcp.db, so the file is restored exactly as it was before that edit. It refuses when the file " +
        "changed after the edit (reverting would overwrite newer work) or when the edit predates the content history. " +
        "The revert itself is recorded as a new edit, so it can be undone too.")]
    public static Task<RevertEditResult> RevertEdit(
        IEditHistoryService history,
        [Description("The id shown by edit_history.")] long id,
        CancellationToken cancellationToken = default)
        => history.RevertAsync(id, cancellationToken);
}
