using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Storage;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class CommandLogTool
{
    [McpServerTool(Name = "command_log"), Description(
        "Read the command log stored in the solution's SQLite database (.visualMCP/visualmcp.db): one row per tool " +
        "call, written as the call arrives — date, time, tool name and its arguments. Use it to see what was done and " +
        "when (a session summary, an audit, 'what did I change yesterday'). Arguments are capped and credential-looking " +
        "values are redacted. Read-only.")]
    public static object CommandLog(
        IVisualMcpStore store,
        [Description("How many rows to return, newest first (default 50, max 5000).")] int limit = 50,
        [Description("Only this tool's calls, e.g. 'apply_rename'.")] string? tool = null,
        [Description("Only rows whose arguments contain this text (case-insensitive).")] string? contains = null)
    {
        if (store.DatabasePath is null && store.EnsureDatabase() is null)
            return new { error = "No solution is loaded, so there is no command log database. Call load_solution first." };

        var entries = store.RecentCommands(limit, tool);
        if (!string.IsNullOrWhiteSpace(contains))
            entries = entries.Where(entry => entry.Arguments is not null &&
                                             entry.Arguments.Contains(contains!, StringComparison.OrdinalIgnoreCase)).ToList();

        return new
        {
            database = store.DatabasePath,
            totalRows = store.CommandCount(),
            returned = entries.Count,
            note = "Times are UTC. Arguments are capped at 1000 characters; credential-looking values are stored as ***.",
            commands = entries.Select(entry => new
            {
                date = entry.OccurredAt.ToString("yyyy-MM-dd"),
                time = entry.OccurredAt.ToString("HH:mm:ss"),
                at = entry.OccurredAt.ToString("O"),
                tool = entry.Tool,
                arguments = entry.Arguments,
            }).ToList(),
        };
    }
}
