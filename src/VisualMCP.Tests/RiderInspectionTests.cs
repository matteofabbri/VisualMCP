using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Languages.CSharp.Inspections.Rider;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The ReSharper-shaped pack: each native analyzer under ReSharper's own id, and the curated coverage map
/// that says which ids map where — the honest half of "the proprietary catalogue".
/// </summary>
public sealed class RiderInspectionTests
{
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source, DiagnosticAnalyzer analyzer)
    {
        using var workspace = FakeWorkspace.Create(("C.cs", source));
        var solution = await workspace.EnsureSolutionLoadedAsync();
        var compilation = await solution!.Projects.Single().GetCompilationAsync();

        var analyzers = compilation!.WithAnalyzers(ImmutableArray.Create(analyzer));
        return await analyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task ConvertToConstant_reports_a_local_assignedOnceWithAConstant()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    int limit = 10;
                    return limit;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new ConvertToConstantLocalAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ConvertToConstant_Local", diagnostic.Id);
    }

    [Fact]
    public async Task ConvertToConstant_ignores_a_local_that_is_written_later()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    int limit = 10;
                    limit++;
                    return limit;
                }
            }
            """;

        Assert.Empty(await AnalyzeAsync(source, new ConvertToConstantLocalAnalyzer()));
    }

    [Fact]
    public async Task LoopVariableChanged_reports_a_write_inside_the_body()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    var total = 0;
                    for (var i = 0; i < 10; i++)
                    {
                        i = i + 2;
                        total += i;
                    }

                    return total;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new LoopVariableChangedAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "LoopVariableIsNeverChangedInsideLoop");
    }

    [Fact]
    public async Task MemberCanBeMadeStatic_reports_a_method_without_instance_state()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                private int _value;

                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int WithState()
                {
                    return _value;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new MemberCanBeMadeStaticAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MemberCanBeMadeStatic_Global", diagnostic.Id);
        Assert.Contains("Add", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RedundantArgument_reports_the_value_equal_to_the_default()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M() => Add(1, 5);

                private int Add(int value, int step = 5) => value + step;
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantArgumentAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RedundantArgument");
    }

    [Fact]
    public async Task EmptyConstructor_reports_the_only_public_parameterless_one()
    {
        const string source = """
            namespace Probe;

            public class C
            {
                public C()
                {
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new EmptyConstructorAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "EmptyConstructor");
    }

    [Fact]
    public async Task RedundantIfElse_reports_opposite_boolean_returns()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool IsPositive(int value)
                {
                    if (value > 0)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantIfElseAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RedundantIfElseBlock");
    }

    [Fact]
    public async Task ConstantCondition_reports_a_computed_constant_but_not_a_literal()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public void M()
                {
                    if (1 == 1) { }

                    while (true) { break; }
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new ConstantConditionAnalyzer());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ConditionIsAlwaysTrueOrFalse", diagnostic.Id);
    }

    [Fact]
    public async Task RedundantBaseConstructorCall_reports_an_explicit_parameterless_base()
    {
        const string source = """
            namespace Probe;

            internal class B
            {
            }

            internal class C : B
            {
                public C() : base()
                {
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, new RedundantBaseConstructorCallAnalyzer());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "RedundantBaseConstructorCall");
    }

    // ── coverage map ────────────────────────────────────────────────────────
    [Fact]
    public async Task The_rider_pack_is_selectable_through_inspect_code()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            public class C
            {
                public C()
                {
                }
            }
            """));

        var report = await new InspectCodeService(workspace)
            .RunAsync(null, null, "rider", "info", null, null, enableAll: true, maxResults: 100, timeoutSeconds: 60);

        Assert.Contains(report.Findings, finding => finding.InspectionId == "EmptyConstructor");
        Assert.Equal("rider", Assert.Single(report.Packs));
    }

    [Fact]
    public async Task The_map_maps_the_eight_native_ids_and_names_the_absent_ones()
    {
        var result = ResultAssert.Json(await new ResharperInspectionMapService().ReportAsync(includeInstall: false));

        Assert.True(result.GetProperty("curatedCount").GetInt32() >= 40);
        Assert.Equal(8, result.GetProperty("byKind").GetProperty("rider").GetInt32());
        Assert.True(result.GetProperty("byKind").GetProperty("none").GetInt32() > 0);

        var uncovered = result.GetProperty("uncovered").EnumerateArray().Select(entry => entry.GetString()).ToList();
        Assert.Contains("UseNameofExpression", uncovered);

        var riderEntries = result.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "rider")
            .Select(entry => entry.GetProperty("id").GetString())
            .ToList();
        Assert.Contains("ConvertToConstant.Local", riderEntries);
        Assert.Contains("LoopVariableIsNeverChangedInsideLoop", riderEntries);
    }

    [Fact]
    public async Task A_missing_riderInstallation_is_reported_not_guessed()
    {
        var result = ResultAssert.Json(await new ResharperInspectionMapService().ReportAsync(
            riderPath: Path.Combine(Path.GetTempPath(), "vmcp-no-rider-" + Guid.NewGuid().ToString("N")),
            includeInstall: true));

        Assert.False(result.GetProperty("install").GetProperty("found").GetBoolean());
    }
}
