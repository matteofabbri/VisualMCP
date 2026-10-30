using Snitch.Languages.CSharp.Refactoring;
using Snitch.Languages.CSharp.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The formatting step and the cleanup that composes the rewrite pipeline: the scope means the same
/// thing in both, each file keeps its own line endings, and a failing step stops the run instead of
/// leaving a half-cleaned file.
/// </summary>
public sealed class CodeCleanupTests
{
    private const string Messy = """
        using System;
        using System.Text;

        public class Probe
        {
        #region Fields
        public int Value;
        #endregion

        public void Run( ){ }
        }
        """;

    private static FormatCodeService Formatter(FakeWorkspace workspace) => new(workspace);

    private static CodeCleanupService Cleanup(FakeWorkspace workspace) => new(
        new SimplifyCodeService(workspace),
        new OptimizeUsingsService(workspace),
        new RemoveRegionsService(workspace),
        new ReorderMembersService(workspace),
        new FormatCodeService(workspace));

    [Fact]
    public async Task Format_reindents_the_file()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Formatter(workspace).Format(
            "document", workspace.PathOf("Probe.cs"), null, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Equal(1, ResultAssert.Json(result).GetProperty("filesModified").GetInt32());
        Assert.Contains("    public int Value;", workspace.Read("Probe.cs"));
    }

    [Fact]
    public async Task Format_dry_run_leaves_the_file_alone()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Formatter(workspace).Format(
            "document", workspace.PathOf("Probe.cs"), null, dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.Equal(1, ResultAssert.Json(result).GetProperty("filesModified").GetInt32());
        Assert.Equal(Messy, workspace.Read("Probe.cs"));
    }

    [Fact]
    public async Task Cleanup_removes_regions_and_unused_usings_and_formats()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "document", workspace.PathOf("Probe.cs"), null, steps: null, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.DoesNotContain("#region", text);
        Assert.DoesNotContain("using System.Text;", text);
        Assert.Contains("    public int Value;", text);
    }

    [Fact]
    public async Task Cleanup_with_only_format_keeps_the_regions()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "document", workspace.PathOf("Probe.cs"), null, steps: "format", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.Contains("#region Fields", text);
        Assert.Contains("    public int Value;", text);
    }

    [Fact]
    public async Task Cleanup_rejects_an_unknown_step()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "solution", null, null, steps: "style,teleport", dryRun: true);

        Assert.Contains("teleport", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Cleanup_reports_the_steps_it_ran_in_order()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "solution", null, null, steps: "usings,format", dryRun: true);
        ResultAssert.Succeeded(result);

        var steps = ResultAssert.Json(result).GetProperty("steps").EnumerateArray()
            .Select(step => step.GetString()).ToList();

        Assert.Equal(new[] { "usings", "format" }, steps);
    }
}
