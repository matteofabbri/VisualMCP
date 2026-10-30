using Snitch.Languages.CSharp.Analysis;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The architecture and quality analyses: layers, cohesion, test quality, argument validation,
/// nullability and the public-API comparison. Each one answers a question the complexity report and
/// the inspection packs do not.
/// </summary>
public sealed class ArchitectureAnalysisTests
{
    [Fact]
    public async Task A_dependency_that_points_up_a_layer_is_a_violation()
    {
        using var workspace = FakeWorkspace.Create(("Layers.cs", """
            namespace App.Web
            {
                public class Controller { }
            }

            namespace App.Data
            {
                public class Repository
                {
                    public App.Web.Controller? Owner;
                }
            }
            """));

        var result = await new CheckLayerViolationsService(workspace).Check("Web,Data", null, 50);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("violationCount").GetInt32());

        var violation = json.GetProperty("violations").EnumerateArray().Single();
        Assert.Equal("Data", violation.GetProperty("from").GetString());
        Assert.Equal("Web", violation.GetProperty("to").GetString());
        Assert.True(violation.GetProperty("line").GetInt32() > 0);
    }

    [Fact]
    public async Task A_dependency_that_points_down_a_layer_is_allowed()
    {
        using var workspace = FakeWorkspace.Create(("Layers.cs", """
            namespace App.Web
            {
                public class Controller
                {
                    public App.Data.Repository? Store;
                }
            }

            namespace App.Data
            {
                public class Repository { }
            }
            """));

        var result = await new CheckLayerViolationsService(workspace).Check("Web,Data", null, 50);
        ResultAssert.Succeeded(result);

        Assert.True(ResultAssert.Json(result).GetProperty("clean").GetBoolean());
    }

    [Fact]
    public async Task The_dependency_graph_exports_as_dot()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "public class C { }"));

        var result = await new ExportDependencyGraphService(workspace).Export("dot", "projects");
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal("dot", json.GetProperty("format").GetString());
        Assert.StartsWith("digraph dependencies {", json.GetProperty("graph").GetString());
    }

    [Fact]
    public async Task Cohesion_reports_a_class_with_two_field_clusters()
    {
        using var workspace = FakeWorkspace.Create(("Split.cs", """
            public class Split
            {
                private int _a;
                private int _b;

                public int ReadA() => _a;
                public int ReadB() => _b;
            }
            """));

        var result = await new GetCohesionMetricsService(workspace).Measure(null, 2, 10);
        ResultAssert.Succeeded(result);

        var entry = Assert.Single(ResultAssert.Json(result).GetProperty("classes").EnumerateArray());
        Assert.Equal("Split", entry.GetProperty("type").GetString());
        Assert.Equal(2, entry.GetProperty("components").GetInt32());
    }

    [Fact]
    public async Task Cohesion_is_one_when_the_members_share_a_field()
    {
        using var workspace = FakeWorkspace.Create(("Cohesive.cs", """
            public class Cohesive
            {
                private int _value;

                public int Read() => _value;
                public void Write(int value) => _value = value;
            }
            """));

        var result = await new GetCohesionMetricsService(workspace).Measure(null, 2, 10);
        ResultAssert.Succeeded(result);

        Assert.Equal(0, ResultAssert.Json(result).GetProperty("flaggedCount").GetInt32());
    }

    [Fact]
    public async Task Test_quality_flags_an_empty_test_without_assertions()
    {
        using var workspace = FakeWorkspace.Create(("Tests.cs", """
            public class SampleTests
            {
                [Fact]
                public void Test1() { }

                [Fact]
                public void Adding_two_numbers_returns_the_sum()
                {
                    Assert.Equal(3, 1 + 2);
                }
            }
            """));

        var result = await new AnalyzeTestQualityService(workspace).Analyze(null, 10);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("testCount").GetInt32());
        Assert.Equal(1, json.GetProperty("flaggedCount").GetInt32());

        var issues = json.GetProperty("tests").EnumerateArray().Single()
            .GetProperty("issues").EnumerateArray().Select(issue => issue.GetString()).ToList();

        Assert.Contains("empty", issues);
        Assert.Contains("no-assert", issues);
        Assert.Contains("non-descriptive-name", issues);
    }

    [Fact]
    public async Task Missing_validation_flags_the_unguarded_parameter_only()
    {
        using var workspace = FakeWorkspace.Create(("Service.cs", """
            public class Service
            {
                public void Unguarded(string name)
                {
                    var length = name.Length;
                }

                public void Guarded(string name)
                {
                    if (name is null) throw new ArgumentNullException(nameof(name));
                    var length = name.Length;
                }
            }
            """));

        var result = await new FindMissingValidationService(workspace).Find(null, 10);
        ResultAssert.Succeeded(result);

        var entry = Assert.Single(ResultAssert.Json(result).GetProperty("methods").EnumerateArray());
        Assert.Equal("Unguarded", entry.GetProperty("method").GetString());
        Assert.Equal("name", entry.GetProperty("unguarded").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Nullability_counts_the_suppressions()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                public int Length(string? value) => value!.Length;
            }
            """));

        var result = await new AnalyzeNullabilityService(workspace).Analyze(null, 10);
        ResultAssert.Succeeded(result);

        Assert.Equal(1, ResultAssert.Json(result).GetProperty("totalSuppressions").GetInt32());
    }

    [Fact]
    public void Api_surface_keeps_the_public_members_and_drops_the_private_ones()
    {
        var surface = FindBreakingChangesService.ApiSurface("""
            public class C
            {
                public int Visible() => 1;
                private int Hidden() => 2;
                internal int AlsoHidden() => 3;
            }
            """);

        Assert.Contains("C::Visible()", surface.Keys);
        Assert.DoesNotContain("C::Hidden()", surface.Keys);
        Assert.DoesNotContain("C::AlsoHidden()", surface.Keys);
    }

    [Fact]
    public void Api_comparison_reports_removed_added_and_changed()
    {
        var before = FindBreakingChangesService.ApiSurface("""
            public class C
            {
                public int Value { get; set; }
                public int Compute(string input) => 1;
            }
            """);

        var after = FindBreakingChangesService.ApiSurface("""
            public class C
            {
                public string Compute(string input) => input;
                public void Added() { }
            }
            """);

        var (removed, added, changed) = FindBreakingChangesService.CompareSurfaces(before, after);

        Assert.Contains("C::P:Value", removed);
        Assert.Contains("C::Added()", added);
        Assert.Contains(changed, entry => entry.Contains("C::Compute(string)", StringComparison.Ordinal));
    }
}
