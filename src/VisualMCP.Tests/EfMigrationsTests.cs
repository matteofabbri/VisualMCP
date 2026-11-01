using Snitch.Execution;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The 'dotnet ef' command line, which is the part of the migrations workflow that can be checked without
/// a project, a database or the dotnet-ef tool installed.
/// </summary>
public sealed class EfMigrationsTests
{
    [Fact]
    public void Pending_model_changes_maps_to_the_EF_command()
    {
        Assert.Equal(
            ["ef", "migrations", "has-pending-model-changes"],
            EfMigrationsService.CommandArguments("pending-model-changes", null, null));
    }

    [Fact]
    public void The_other_actions_keep_their_arguments()
    {
        Assert.Equal(["ef", "migrations", "list"], EfMigrationsService.CommandArguments("list", null, null));
        Assert.Equal(["ef", "migrations", "add", "Initial"],
            EfMigrationsService.CommandArguments("add", "Initial", null));
        Assert.Equal(["ef", "migrations", "script", "--output", "out.sql"],
            EfMigrationsService.CommandArguments("script", null, "out.sql"));
        Assert.Equal(["ef", "dbcontext", "info"], EfMigrationsService.CommandArguments("info", null, null));
        Assert.Equal(["ef", "dbcontext", "list"], EfMigrationsService.CommandArguments("dbcontext-list", null, null));
    }

    [Fact]
    public void Diff_is_a_script_between_two_migrations()
    {
        Assert.Equal(["ef", "migrations", "script", "0", "Latest"],
            EfMigrationsService.CommandArguments("diff", null, null, "0", "Latest"));

        Assert.Equal(["ef", "migrations", "script", "From", "--output", "delta.sql"],
            EfMigrationsService.CommandArguments("diff", null, "delta.sql", "From"));
    }
}
