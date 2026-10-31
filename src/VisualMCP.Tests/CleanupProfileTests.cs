using Snitch.Languages.CSharp.Refactoring;
using Snitch.Languages.CSharp.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Cleanup profiles: ReSharper's named step lists. The built-ins resolve without a file, a team profile
/// is read from .visualMCP/cleanup-profiles.json, an unknown name answers with the catalogue, and
/// saveProfileAs writes the effective list back so the next call can name it.
/// </summary>
public sealed class CleanupProfileTests
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

    private static CodeCleanupService Cleanup(FakeWorkspace workspace) => new(
        new SimplifyCodeService(workspace),
        new OptimizeUsingsService(workspace),
        new RemoveRegionsService(workspace),
        new ReorderMembersService(workspace),
        new FormatCodeService(workspace),
        workspace);

    [Fact]
    public async Task ReformatProfile_RunsOnlyTheFormatter()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "document", workspace.PathOf("Probe.cs"), null, steps: null, dryRun: false, profile: "reformat");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.Contains("#region Fields", text);
        Assert.Contains("using System.Text;", text);
        Assert.Contains("    public int Value;", text);

        var json = ResultAssert.Json(result);
        Assert.Equal("reformat", json.GetProperty("profile").GetString());
        Assert.Equal(new[] { "format" }, json.GetProperty("steps").EnumerateArray()
            .Select(step => step.GetString()).ToList());
    }

    [Fact]
    public async Task ATeamProfile_IsReadFromTheSolutionStore()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));
        var store = Path.Combine(workspace.RootDirectory, ".visualMCP", "cleanup-profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store,
            """{ "profiles": { "usingsOnly": { "steps": ["usings"] } } }""");

        var result = await Cleanup(workspace).Cleanup(
            "document", workspace.PathOf("Probe.cs"), null, steps: null, dryRun: false, profile: "usingsonly");
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.DoesNotContain("using System.Text;", text);
        Assert.Contains("#region Fields", text);

        var json = ResultAssert.Json(result);
        Assert.Equal("usingsOnly", json.GetProperty("profile").GetString());
        Assert.Equal(store, json.GetProperty("profileFile").GetString());
    }

    [Fact]
    public async Task AnUnknownProfile_ReturnsTheCatalogue()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "solution", null, null, steps: null, dryRun: true, profile: "teleport");

        Assert.Contains("Unknown cleanup profile", ResultAssert.Error(result));
        var available = ResultAssert.Json(result).GetProperty("availableProfiles");
        Assert.True(available.TryGetProperty("full", out _));
        Assert.True(available.TryGetProperty("safe", out _));
    }

    [Fact]
    public async Task SaveProfileAs_WritesTheEffectiveStepList()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var saved = await Cleanup(workspace).Cleanup(
            "document", workspace.PathOf("Probe.cs"), null, steps: null, dryRun: false,
            profile: "safe", saveProfileAs: "team");
        ResultAssert.Succeeded(saved);

        var store = Path.Combine(workspace.RootDirectory, ".visualMCP", "cleanup-profiles.json");
        Assert.True(File.Exists(store));
        var json = File.ReadAllText(store);
        Assert.Contains("\"team\"", json);
        Assert.Contains("usings", json);

        var reused = await Cleanup(workspace).Cleanup(
            "solution", null, null, steps: null, dryRun: true, profile: "team");
        ResultAssert.Succeeded(reused);
        Assert.Equal("team", ResultAssert.Json(reused).GetProperty("profile").GetString());
    }

    [Fact]
    public async Task StepsAndProfileTogetherAreRefused()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "solution", null, null, steps: "format", dryRun: true, profile: "safe");

        Assert.Contains("not both", ResultAssert.Error(result));
    }

    [Fact]
    public async Task AProfileCannotShadowABuiltInName()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", Messy));

        var result = await Cleanup(workspace).Cleanup(
            "solution", null, null, steps: "format", dryRun: true, saveProfileAs: "full");

        Assert.Contains("built-in", ResultAssert.Error(result));
    }
}
