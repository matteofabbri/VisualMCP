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

    [Fact]
    public async Task Model_diff_compares_the_snapshot_with_the_entity_class_field_by_field()
    {
        using var workspace = FakeWorkspace.Create(
            ("ShopModelSnapshot.cs", """
                namespace Shop;

                public class ShopContextModelSnapshot : ModelSnapshot
                {
                    protected override void BuildModel(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity("Shop.Order", b =>
                        {
                            b.Property<int>("Id");
                            b.Property<int>("CustomerName");
                            b.Property<int>("Total");
                            b.ToTable("Orders");
                        });
                    }
                }

                public class Order
                {
                    public int Id { get; set; }
                    public string CustomerName { get; set; } = "";
                    public string Note { get; set; } = "";
                }
                """));

        var service = new EfMigrationsService(workspace);
        var result = ResultAssert.Json(
            await service.RunAsync(null, "model-diff", null, null, null, false, 60, null, null));

        Assert.Equal("ShopContextModelSnapshot", result.GetProperty("snapshotClass").GetString());
        Assert.True(result.GetProperty("drifted").GetBoolean());

        var entity = Assert.Single(result.GetProperty("entities").EnumerateArray().ToList());
        Assert.Equal("Shop.Order", entity.GetProperty("type").GetString());
        Assert.Equal("Orders", entity.GetProperty("table").GetString());
        Assert.True(entity.GetProperty("resolved").GetBoolean());

        var added = entity.GetProperty("addedInTheModel").EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Equal(["Note"], added);

        var notOnType = entity.GetProperty("notOnTheEntityType").EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Equal(["Total"], notOnType);

        var mismatch = Assert.Single(entity.GetProperty("typeMismatches").EnumerateArray().ToList());
        Assert.Equal("CustomerName", mismatch.GetProperty("property").GetString());
        Assert.Equal("string", mismatch.GetProperty("model").GetString());
        Assert.Equal("int", mismatch.GetProperty("snapshot").GetString());
    }

    [Fact]
    public async Task Model_diff_says_so_when_the_project_has_no_snapshot()
    {
        using var workspace = FakeWorkspace.Create(("Plain.cs", "namespace Shop { public class Marker { } }"));

        var service = new EfMigrationsService(workspace);
        var error = ResultAssert.Error(
            await service.RunAsync(null, "model-diff", null, null, null, false, 60, null, null));

        Assert.Contains("ModelSnapshot", error);
    }
}
