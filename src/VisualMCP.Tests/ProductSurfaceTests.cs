using Snitch.Execution;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Languages.CSharp.Navigation;
using VisualMCP.Analysis;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The product-surface wave: the codebase map, the server's own capability report, the .editorconfig
/// option authoring and the packaging wrappers (validation only — a real publish/push needs a feed).
/// </summary>
public sealed class ProductSurfaceTests
{
    [Fact]
    public async Task SolutionMap_DescribesProjectsAreasAndEntryPoints()
    {
        const string source = """
            namespace Probe.Area;

            internal class Widget
            {
                public void First() { }
                public void Second() { }
            }
            """;

        var workspace = FakeWorkspace.Create(("Widget.cs", source));
        using var scope = workspace;
        var map = new SolutionMapService(workspace);

        var result = ResultAssert.Json(await map.MapAsync(null, 20, 10));
        Assert.Equal(1, result.GetProperty("projectCount").GetInt32());

        var project = result.GetProperty("projects")[0];
        Assert.Equal("Probe", project.GetProperty("name").GetString());
        Assert.False(project.GetProperty("isTest").GetBoolean());

        var area = result.GetProperty("areas")[0];
        Assert.Equal("Probe.Area", area.GetProperty("namespace").GetString());
        Assert.Equal(1, area.GetProperty("types").GetInt32());
        Assert.Equal("Widget", area.GetProperty("largest")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SolutionMap_FindsTopLevelStatements()
    {
        var workspace = FakeWorkspace.Create(("Program.cs", "System.Console.WriteLine(\"hi\");"));
        using var scope = workspace;
        var map = new SolutionMapService(workspace);

        var result = ResultAssert.Json(await map.MapAsync(null, 20, 10));
        Assert.Contains(result.GetProperty("entryPoints").EnumerateArray(),
            entry => entry.GetProperty("kind").GetString() == "topLevelStatements");
    }

    [Fact]
    public void CapabilityReport_ReadsTheLiveToolInventoryAndTheCatalogues()
    {
        var report = ResultAssert.Json(new CapabilityReportService().Report());

        Assert.True(report.GetProperty("toolCount").GetInt32() >= 295,
            "the live tool count must include the latest batch: " + report.GetProperty("toolCount").GetInt32());

        var byArea = report.GetProperty("byArea");
        Assert.True(byArea.TryGetProperty("CSharp", out _), "the C# area must be present");
        Assert.True(byArea.TryGetProperty("Debugging", out _), "the debugger area must be present");

        var catalogues = report.GetProperty("catalogues");
        Assert.Equal(9, catalogues.GetProperty("xamlRules").GetArrayLength());
        Assert.Equal(18, catalogues.GetProperty("contextActions").GetArrayLength());
    }

    [Fact]
    public async Task ConfigureFormatting_WritesAnOptionAndRejectsAnUnknownOne()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var scope = workspace;
        var service = new InspectionConfigurationService(workspace);

        var result = ResultAssert.Json(await service.ConfigureOptionAsync("csharp_prefer_braces", "true", "solution", null, null));
        Assert.True(result.GetProperty("changed").GetBoolean());

        var text = File.ReadAllText(Path.Combine(workspace.RootDirectory, ".editorconfig"));
        Assert.Contains("csharp_prefer_braces = true", text);

        Assert.Contains("is not an .editorconfig option",
            ResultAssert.Error(await service.ConfigureOptionAsync("nonsense_option", "1")));
        Assert.Contains("single-line value",
            ResultAssert.Error(await service.ConfigureOptionAsync("indent_size", "4\n")));
    }

    [Fact]
    public async Task Packaging_ValidatesProjectsAndReferencesWithoutRunningDotnet()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var scope = workspace;
        var service = new BuildPackagingService(workspace);

        Assert.Contains("not found", ResultAssert.Error(await service.PublishAsync("Nope")));

        var reference = ResultAssert.Json(await service.AddProjectReferenceAsync("Probe", "Missing"));
        Assert.Contains("neither a project", reference.GetProperty("error").GetString());

        Assert.Contains("required", ResultAssert.Error(await service.PushAsync(string.Empty)));
    }
}
