using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Storage;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class LocalHistoryTool
{
    [McpServerTool(Name = "local_history"), Description(
        "ReSharper's Local History: whole-file snapshots in .visualMCP/visualmcp.db, independent of commits. " +
        "action=snapshot walks the solution's source files and captures each one whose content differs from its " +
        "newest snapshot (the text is gzipped and deduplicated on its hash; each file keeps the newest 50). " +
        "action=history lists a file's snapshots, newest first, with the current hash so the reader can see which " +
        "one is the file as it stands. action=diff shows a snapshot against the current file, or against another " +
        "snapshot with otherId. action=restore writes a snapshot back: the text it replaces is snapshotted first " +
        "and the restore is recorded in the edit history, so it is reversible. Build and VCS folders are pruned, and " +
        "files over 1.5 MB are skipped.")]
    public static async Task<object> LocalHistory(
        ILocalHistoryService history,
        [Description("snapshot | history | diff | restore (default: snapshot).")] string action = "snapshot",
        [Description("The file for history, diff and restore (absolute or relative).")] string? path = null,
        [Description("The snapshot id for diff and restore, as history reports it.")] long? id = null,
        [Description("For diff: compare the snapshot with this other snapshot instead of the current file.")] long? otherId = null,
        [Description("For history: how many snapshots to return (default 20, max 500).")] int limit = 20,
        [Description("For restore: true (default) previews the diff only; pass false to write the file.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "snapshot":
                return await history.SnapshotAsync(cancellationToken);

            case "history":
                return string.IsNullOrWhiteSpace(path)
                    ? new { error = "history needs a path." }
                    : await history.HistoryAsync(path, limit);

            case "diff":
                if (string.IsNullOrWhiteSpace(path) || id is null)
                    return new { error = "diff needs path and id." };
                return await history.DiffAsync(path, id.Value, otherId, cancellationToken);

            case "restore":
                if (string.IsNullOrWhiteSpace(path) || id is null)
                    return new { error = "restore needs path and id." };
                return await history.RestoreAsync(path, id.Value, dryRun, cancellationToken);

            default:
                return new { error = "action must be snapshot, history, diff or restore." };
        }
    }
}
