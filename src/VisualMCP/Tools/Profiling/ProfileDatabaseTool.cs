using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Profiling.Analysis;

namespace VisualMCP.Tools.Profiling;

[McpServerToolType]
public static class ProfileDatabaseTool
{
    [McpServerTool(Name = "profile_database"), Description(
        "The Database analysis: read a trace collected with the database preset (EF Core, SqlClient, Npgsql, MySql, " +
        "SQLite, Oracle or Db2 EventSource) and report what the data access did — every command with its text and " +
        "duration, the slowest single commands, the normalised SHAPES that cost the most (literals replaced, so the " +
        "same query with different arguments groups together), connection opens, and the findings those numbers imply: " +
        "a slow statement, the N+1 signature (many fast executions of one shape), the share of the window spent in the " +
        "database. When the trace holds no database event it says which providers it DID contain and the exact " +
        "profile_collect call that fixes it, instead of returning an empty answer. Use profile_collect " +
        "profile=database first, or profile_events to see the provider schemas.")]
    public static async Task<object> Database(
        IDatabaseTraceService database,
        [Description("Path to the .nettrace file (collected with profile_collect profile=database).")] string tracePath,
        [Description("How many slowest commands and command shapes to return (1-200).")] int top = 15,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => database.Analyze(tracePath, top), cancellationToken);
        }
        catch (Exception ex)
        {
            return ProfilingToolHelpers.Error(ex);
        }
    }
}
