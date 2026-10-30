using Snitch.Languages.CSharp.Inspections;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The inspection baseline: fingerprints compared by id + file + message (not line, or every edit
/// above a finding would report it as new), and a check that refuses to run the packs when there is
/// nothing to compare against.
/// </summary>
public sealed class InspectionBaselineTests
{
    /// <summary>The baseline actions under test never run the inspection engine.</summary>
    private sealed class NoInspections : IInspectCodeService
    {
        public Task<InspectionReport> RunAsync(
            string? projectName, string? filePath, string packs, string minSeverity, string? ids,
            string? excludeIds, bool enableAll, int maxResults, int timeoutSeconds) =>
            throw new InvalidOperationException("the inspection engine must not run for this action");
    }

    [Fact]
    public void Compare_reports_what_is_new_and_what_is_fixed()
    {
        var baseline = new[] { "RCS1|a.cs|one", "RCS2|b.cs|two" };
        var current = new[] { "RCS2|b.cs|two", "CA1|c.cs|three" };

        var (added, fixedFindings) = InspectionBaselineService.CompareFindings(baseline, current);

        Assert.Equal(new[] { "CA1|c.cs|three" }, added);
        Assert.Equal(new[] { "RCS1|a.cs|one" }, fixedFindings);
    }

    [Fact]
    public void Compare_is_case_insensitive_on_the_path()
    {
        var (added, fixedFindings) = InspectionBaselineService.CompareFindings(
            new[] { "RCS1|Src/A.cs|one" }, new[] { "RCS1|src/a.cs|one" });

        Assert.Empty(added);
        Assert.Empty(fixedFindings);
    }

    [Fact]
    public async Task Show_without_a_baseline_is_a_note()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var service = new InspectionBaselineService(new NoInspections(), store, workspace);

        var result = await service.Run("show", null, "", "info", null, null, 200, 120);

        Assert.Contains("No baseline stored", ResultAssert.Json(result).GetProperty("note").GetString());
    }

    [Fact]
    public async Task Check_without_a_baseline_fails_before_running_the_packs()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);
        var service = new InspectionBaselineService(new NoInspections(), store, workspace);

        // NoInspections throws if it is ever asked to run: reaching the error proves the baseline is
        // read first, instead of burning an inspection run to discover there is nothing to compare.
        var result = await service.Run("check", null, "", "info", null, null, 200, 120);

        Assert.Contains("run action=create first", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Show_reads_the_stored_baseline()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var store = new VisualMcpStore(workspace);

        store.Set("inspection-baseline:*", """
            {
              "CreatedAt": "2026-10-29T10:00:00.0000000+00:00",
              "Project": "*",
              "Packs": "roslynator",
              "Count": 2,
              "Findings": ["RCS1|a.cs|one", "RCS2|b.cs|two"],
              "ByInspection": { "RCS1": 1, "RCS2": 1 }
            }
            """, "stamp");

        var service = new InspectionBaselineService(new NoInspections(), store, workspace);
        var result = await service.Run("show", null, "", "info", null, null, 200, 120);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.Equal(1, json.GetProperty("byInspection").GetProperty("RCS1").GetInt32());
    }
}
