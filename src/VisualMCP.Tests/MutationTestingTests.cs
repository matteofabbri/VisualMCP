using Snitch.Languages.CSharp.Testing;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// run_mutation_tests parses Stryker's mutation-report.json: the shape is pinned here so the tool's
/// numbers (score, killed/survived/no-coverage, worst files) do not drift with a report version.
/// </summary>
public sealed class MutationTestingTests
{
    private const string Report = """
        {
          "schemaVersion": "1.0",
          "thresholds": { "high": 90, "low": 60, "break": 50 },
          "files": {
            "src/A.cs": {
              "language": "csharp",
              "mutants": [
                { "id": "1", "mutatorName": "Arithmetic", "replacement": "-", "status": "Killed",
                  "location": { "start": { "line": 10, "column": 9 } } },
                { "id": "2", "mutatorName": "Equality", "replacement": "!=", "status": "Survived",
                  "location": { "start": { "line": 12, "column": 9 } } },
                { "id": "3", "mutatorName": "Logical", "replacement": "||", "status": "NoCoverage",
                  "location": { "start": { "line": 15, "column": 9 } } }
              ]
            },
            "src/B.cs": {
              "language": "csharp",
              "mutants": [
                { "id": "4", "mutatorName": "Arithmetic", "replacement": "*", "status": "Timeout",
                  "location": { "start": { "line": 5, "column": 1 } } },
                { "id": "5", "mutatorName": "String", "replacement": "+", "status": "Ignored" },
                { "id": "6", "mutatorName": "Block", "replacement": "{}", "status": "CompileError" }
              ]
            }
          }
        }
        """;

    [Fact]
    public void TheReportBecomesAScoreAndCounts()
    {
        var summary = ResultAssert.Json(MutationTestingService.Parse(Report, "MyApp.Tests"));

        Assert.Equal("MyApp.Tests", summary.GetProperty("project").GetString());
        Assert.Equal(50d, summary.GetProperty("mutationScore").GetDouble());

        var totals = summary.GetProperty("totals");
        Assert.Equal(6, totals.GetProperty("mutants").GetInt32());
        Assert.Equal(1, totals.GetProperty("killed").GetInt32());
        Assert.Equal(1, totals.GetProperty("survived").GetInt32());
        Assert.Equal(1, totals.GetProperty("timedOut").GetInt32());
        Assert.Equal(1, totals.GetProperty("noCoverage").GetInt32());
        Assert.Equal(1, totals.GetProperty("ignored").GetInt32());
        Assert.Equal(1, totals.GetProperty("compileErrors").GetInt32());

        var thresholds = summary.GetProperty("thresholds");
        Assert.Equal(90, thresholds.GetProperty("high").GetInt32());
        Assert.Equal(50, thresholds.GetProperty("breakAt").GetInt32());
    }

    [Fact]
    public void TheWorstFilesAndTheSurvivingMutantsAreNamed()
    {
        var summary = ResultAssert.Json(MutationTestingService.Parse(Report, "MyApp.Tests"));

        Assert.Equal(2, summary.GetProperty("filesWithMutants").GetInt32());

        var worst = summary.GetProperty("worstFiles");
        Assert.Equal("src/A.cs", worst[0].GetProperty("file").GetString());
        Assert.Equal(1, worst[0].GetProperty("survived").GetInt32());

        var survivors = summary.GetProperty("survivedMutants");
        Assert.Equal(2, survivors.GetArrayLength()); // the survivor and the never-covered mutant
        Assert.Equal("src/A.cs", survivors[0].GetProperty("file").GetString());
        Assert.Equal(12, survivors[0].GetProperty("line").GetInt32());
        Assert.Equal("Equality", survivors[0].GetProperty("mutator").GetString());
    }

    [Fact]
    public void AnEmptyReportSaysNoMutantWasGenerated()
    {
        var summary = ResultAssert.Json(MutationTestingService.Parse("""{"schemaVersion":"1.0","files":{}}""", "Tests"));

        Assert.Equal(0d, summary.GetProperty("mutationScore").GetDouble());
        Assert.Equal(0, summary.GetProperty("totals").GetProperty("mutants").GetInt32());
        Assert.Contains("No mutant was generated", summary.GetProperty("note").GetString());
    }
}
