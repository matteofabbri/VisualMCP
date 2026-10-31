using Snitch.Languages.CSharp.Testing;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// verify_change compares two test runs and parses TRX. The parts that decide a verdict are pure, so
/// they are pinned here without a build: a regression is a test that passed before and does not now.
/// </summary>
public sealed class VerifyChangeTests
{
    private static Dictionary<string, string> Run(params (string Name, string Outcome)[] tests) =>
        tests.ToDictionary(test => test.Name, test => test.Outcome, StringComparer.Ordinal);

    [Fact]
    public void A_test_that_passed_before_and_fails_now_is_a_regression()
    {
        var baseline = Run(("A", "Passed"), ("B", "Failed"), ("C", "Passed"));
        var current = Run(("A", "Passed"), ("B", "Passed"), ("C", "Failed"));

        var comparison = VerifyChangeService.Compare(baseline, current);

        Assert.Equal("regression", comparison.Verdict);
        Assert.Single(comparison.Regressions);
        Assert.Contains("C", comparison.Regressions[0]);
        Assert.Single(comparison.Fixes);
        Assert.Contains("B", comparison.Fixes[0]);
        Assert.Equal(1, comparison.UnchangedPassed);
        Assert.Empty(comparison.StillFailing);
    }

    [Fact]
    public void PreExistingFailures_do_not_make_the_change_a_regression()
    {
        var baseline = Run(("A", "Passed"), ("B", "Failed"));
        var current = Run(("A", "Passed"), ("B", "Failed"));

        var comparison = VerifyChangeService.Compare(baseline, current);

        Assert.Equal("behaviour-preserved", comparison.Verdict);
        Assert.Single(comparison.StillFailing);
        Assert.Empty(comparison.Regressions);
    }

    [Fact]
    public void New_and_removed_tests_are_reported_separately()
    {
        var baseline = Run(("A", "Passed"), ("Gone", "Passed"));
        var current = Run(("A", "Passed"), ("Added", "Passed"));

        var comparison = VerifyChangeService.Compare(baseline, current);

        Assert.Equal("behaviour-preserved", comparison.Verdict);
        Assert.Equal(["Added"], comparison.NewTests);
        Assert.Equal(["Gone"], comparison.RemovedTests);
    }

    [Fact]
    public void An_empty_run_is_unverified_rather_than_preserved()
    {
        var comparison = VerifyChangeService.Compare(Run(("A", "Passed")), Run());

        Assert.Equal("unverified", comparison.Verdict);
        Assert.Equal(["A"], comparison.RemovedTests);
    }

    [Fact]
    public void Trx_results_become_name_to_outcome_pairs_and_the_last_attempt_wins()
    {
        const string trx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Probe.Calculator.Add" outcome="Passed" />
                <UnitTestResult testName="Probe.Calculator.Divide" outcome="Failed" />
                <UnitTestResult testName="Probe.Calculator.Retry" outcome="Failed" />
                <UnitTestResult testName="Probe.Calculator.Retry" outcome="Passed" />
              </Results>
            </TestRun>
            """;

        var outcomes = VerifyChangeService.ParseTrx(trx);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal("Passed", outcomes["Probe.Calculator.Add"]);
        Assert.Equal("Failed", outcomes["Probe.Calculator.Divide"]);
        Assert.Equal("Passed", outcomes["Probe.Calculator.Retry"]);
    }

    [Fact]
    public void Garbage_in_Trx_yields_no_results_instead_of_a_crash()
    {
        Assert.Empty(VerifyChangeService.ParseTrx("not xml"));
    }
}
