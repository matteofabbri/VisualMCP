using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;

namespace VisualMCP.Tools.CSharp.EfCore;

[McpServerToolType]
public static class EfMigrationsTool
{
    [McpServerTool(Name = "ef_migrations"), Description(
        "The EF Core migrations workflow, through the project's own 'dotnet ef' tool: list the migrations, " +
        "add one (name), script them (optionally to a file), diff two migrations (the SQL schema delta between " +
        "fromMigration and toMigration), remove the last, apply them (update, optionally " +
        "to a name), show the DbContext info, list contexts, or check whether the model has changed since the " +
        "last migration (pending-model-changes, EF Core 8+, reported as the boolean pendingModelChanges). " +
        "Returns the exact command, exit code, stdout and stderr as structured data — no shell transcript to " +
        "parse. The C# model (DbContext, entities, configurations, migration classes) is analysed by the " +
        "normal tools; this runs the tooling around it.")]
    public static Task<object> EfMigrations(
        IEfMigrationsService ef,
        [Description("The migrations project (exact name from the solution). Omit when the solution has exactly one.")] string? projectName = null,
        [Description("list | add | script | diff | remove | update | info | dbcontext-list | pending-model-changes")] string action = "list",
        [Description("Migration name, for add (required) and update (optional target).")] string? name = null,
        [Description("Output file for script/diff.")] string? output = null,
        [Description("Startup project, when it differs from the migrations project.")] string? startupProject = null,
        [Description("Pass --no-build (faster when the project was just built).")] bool noBuild = false,
        [Description("Timeout in seconds (default 120, max 900).")] int timeoutSeconds = 120,
        [Description("diff: the migration to start from ('0' for an empty database).")] string? fromMigration = null,
        [Description("diff: the migration to end at (omit for the latest).")] string? toMigration = null)
        => ef.RunAsync(projectName, action, name, output, startupProject, noBuild, timeoutSeconds, fromMigration, toMigration);
}
