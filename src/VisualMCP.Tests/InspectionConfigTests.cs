using Snitch.Languages.CSharp.Inspections;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Inspection administration: the .editorconfig key is upserted (never duplicated) at the right
/// scope, and a suppression lands on the member with the compiler as the gate.
/// </summary>
public sealed class InspectionConfigTests
{
    private const string Source = """
        namespace Probe;

        internal class C
        {
            public void M() { }
        }
        """;

    private static (FakeWorkspace Workspace, InspectionConfigurationService Service) NewInspections()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Source));
        return (workspace, new InspectionConfigurationService(workspace));
    }

    [Fact]
    public async Task ConfigureInspection_WritesTheKeyAtTheSolutionScopeAndReplacesItOnTheSecondCall()
    {
        var (workspace, service) = NewInspections();
        using var scope = workspace;

        var first = ResultAssert.Json(await service.ConfigureAsync("CA1822", "none", "solution", null, null));
        Assert.True(first.GetProperty("changed").GetBoolean());
        Assert.Equal("[*.cs]", first.GetProperty("section").GetString());

        var editorConfig = Path.Combine(workspace.RootDirectory, ".editorconfig");
        Assert.True(File.Exists(editorConfig));
        var text = File.ReadAllText(editorConfig);
        Assert.Contains("[*.cs]", text);
        Assert.Contains("dotnet_diagnostic.CA1822.severity = none", text);

        var second = ResultAssert.Json(await service.ConfigureAsync("CA1822", "warning", "solution", null, null));
        Assert.Equal("none", second.GetProperty("previous").GetString());

        var updated = File.ReadAllText(editorConfig);
        Assert.Contains("dotnet_diagnostic.CA1822.severity = warning", updated);
        Assert.DoesNotContain("severity = none", updated);
        Assert.Single(updated.Split('\n').Where(line => line.Contains("dotnet_diagnostic.CA1822")));
    }

    [Fact]
    public async Task ConfigureInspection_ScopesAFileSectionAndRejectsBadInput()
    {
        var (workspace, service) = NewInspections();
        using var scope = workspace;

        var file = ResultAssert.Json(await service.ConfigureAsync(
            "IDE0001", "suggestion", "file", workspace.PathOf("C.cs"), null));
        Assert.Equal("[C.cs]", file.GetProperty("section").GetString());
        Assert.Contains("[C.cs]", File.ReadAllText(Path.Combine(workspace.RootDirectory, ".editorconfig")));

        Assert.Contains("is not a diagnostic id", ResultAssert.Error(await service.ConfigureAsync("nope", "none")));
        Assert.Contains("is not a severity", ResultAssert.Error(await service.ConfigureAsync("CA1822", "loud")));
        Assert.Contains("Unknown scope", ResultAssert.Error(await service.ConfigureAsync("CA1822", "none", "folder")));
    }

    [Fact]
    public async Task SuppressFinding_WrapsTheMemberWithPragmas()
    {
        var (workspace, service) = NewInspections();
        using var scope = workspace;

        var raw = ResultAssert.Json(await service.SuppressAsync(
            "CA1822", "pragma", workspace.PathOf("C.cs"), 5, "static does not matter here"));
        Assert.True(raw.TryGetProperty("suppressed", out _), raw.GetRawText());
        Assert.Equal("M()", raw.GetProperty("symbol").GetString());

        var lines = File.ReadAllLines(workspace.PathOf("C.cs"));
        var disable = Array.FindIndex(lines, line => line.Contains("#pragma warning disable CA1822"));
        var restore = Array.FindIndex(lines, line => line.Contains("#pragma warning restore CA1822"));
        var method = Array.FindIndex(lines, line => line.Contains("public void M()"));

        Assert.True(disable >= 0 && disable < method, "the disable pragma must come before the method");
        Assert.True(restore > method, "the restore pragma must come after the method");
        Assert.Contains("static does not matter here", lines[disable]);
    }

    [Fact]
    public async Task SuppressFinding_PutsASuppressMessageAttributeOnTheMember()
    {
        var (workspace, service) = NewInspections();
        using var scope = workspace;

        var result = ResultAssert.Json(await service.SuppressAsync(
            "CA1822", "attribute", workspace.PathOf("C.cs"), 5, "instance API by design"));
        Assert.Equal("attribute", result.GetProperty("strategy").GetString());

        var text = workspace.Read("C.cs");
        Assert.Contains(
            "[System.Diagnostics.CodeAnalysis.SuppressMessage(\"Design\", \"CA1822\", Justification = \"instance API by design\")]",
            text);
    }

    [Fact]
    public async Task SuppressFinding_DryRunWritesNothingAndABadLineIsRefused()
    {
        var (workspace, service) = NewInspections();
        using var scope = workspace;

        var before = workspace.Read("C.cs");
        var preview = ResultAssert.Json(await service.SuppressAsync(
            "CA1822", "pragma", workspace.PathOf("C.cs"), 5, null, dryRun: true));
        Assert.False(preview.GetProperty("applied").GetBoolean());
        Assert.Equal(before, workspace.Read("C.cs"));

        Assert.Contains("is outside",
            ResultAssert.Error(await service.SuppressAsync("CA1822", "pragma", workspace.PathOf("C.cs"), 9999, null)));
    }
}
