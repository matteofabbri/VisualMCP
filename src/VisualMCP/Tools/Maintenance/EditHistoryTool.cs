using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Storage;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class EditHistoryTool
{
    [McpServerTool(Name = "edit_history"), Description(
        "Read the solution's edit history from .visualMCP/visualmcp.db: one row per file this server wrote, with the " +
        "tool that wrote it, the rules that produced the change and short content hashes before and after. Use it to " +
        "audit 'what changed and when', to spot a rewrite that reported itself without changing the text (identical " +
        "hashes), and as the anchor for a revert. Only this server's writes are recorded; a file edited in the IDE or " +
        "by git does not appear. Times are UTC. Read-only.")]
    public static object EditHistory(
        IVisualMcpStore store,
        [Description("How many rows to return, newest first (default 50, max 5000).")] int limit = 50,
        [Description("Only this file's edits (absolute path).")] string? file = null)
    {
        if (store.DatabasePath is null && store.EnsureDatabase() is null)
            return new { error = "No solution is loaded, so there is no edit history. Call load_solution first." };

        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;

        var entries = store.RecentEdits(limit, file);
        return new
        {
            database = store.DatabasePath,
            totalRows = store.EditCount(),
            returned = entries.Count,
            note = "Hashes are the first 16 hex characters of SHA-256: equal values mean the write left the text unchanged.",
            edits = entries.Select(entry => new
            {
                id = entry.Id,
                date = entry.AppliedAt.ToString("yyyy-MM-dd"),
                time = entry.AppliedAt.ToString("HH:mm:ss"),
                at = entry.AppliedAt.ToString("O"),
                tool = entry.Tool,
                rules = entry.Rules,
                file = entry.File,
                before = entry.BeforeHash,
                after = entry.AfterHash,
                changed = entry.ContentChanged,
                revertible = entry.Revertible,
                chars = new { before = entry.BeforeChars, after = entry.AfterChars },
            }).ToList(),
        };
    }
}
