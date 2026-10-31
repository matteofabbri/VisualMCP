using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Debugging;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class TestWorkflowExtrasTools
{
    [McpServerTool(Name = "list_tests"), Description(
        "Discover the tests from the source without running them: class, method, framework (xUnit/NUnit/MSTest), " +
        "whether the test is parameterized, and the inline data rows of [InlineData]/[TestCase]/[DataRow]. MemberData/" +
        "TestCaseSource/DynamicData are named as dynamic (the runner produces their cases). Narrow by project or " +
        "with a substring filter on Class.Method.")]
    public static Task<object> ListTests(
        ITestDiscoveryService discovery,
        [Description("Optional: restrict to one project by name.")] string? projectName = null,
        [Description("Optional: substring of Class.Method (case-insensitive).")] string? filter = null,
        [Description("Maximum tests to return (default 500, max 5000).")] int maxResults = 500)
        => discovery.ListAsync(projectName, filter, maxResults);

    [McpServerTool(Name = "find_flaky_tests"), Description(
        "Flaky detection: runs the selected tests several times and reports the ones whose outcome changed between " +
        "runs, with the pattern (Passed/Failed/Skipped per run). Tests that never passed are reported apart as " +
        "alwaysFailing — broken, not flaky. Slow by design: every iteration is a full build-and-run.")]
    public static Task<object> FindFlakyTests(
        IFlakyTestsService flaky,
        [Description("Optional: the test project. Omit when the solution has exactly one test project.")] string? projectName = null,
        [Description("Optional: a test name (fragment or fully qualified).")] string? testName = null,
        [Description("Optional: a test class name.")] string? className = null,
        [Description("Optional: a raw dotnet test --filter expression (wins over testName/className).")] string? filter = null,
        [Description("How many runs to compare (default 3, min 2, max 10).")] int iterations = 3,
        [Description("Timeout per run in seconds (default 300, max 1800).")] int timeoutSeconds = 300)
        => flaky.FindAsync(projectName, testName, className, filter, iterations, timeoutSeconds);

    [McpServerTool(Name = "run_affected_tests"), Description(
        "Test impact analysis that also runs: git diff against baseRef, the symbols the changed files declare, the " +
        "test methods that reference them — then runs exactly those tests (the filter is composed for you) and " +
        "returns the run result. When no test covers the change it answers no-affected-tests, which can also mean " +
        "the change is untested.")]
    public static Task<object> RunAffectedTests(
        IAffectedTestsService affected,
        [Description("The revision to compare against (default HEAD).")] string? baseRef = null,
        [Description("Optional: only symbols declared in this project take part in the analysis.")] string? projectName = null,
        [Description("Optional: the test project to run (default: the solution's single test project).")] string? testProjectName = null,
        [Description("Timeout for the run in seconds (default 300).")] int timeoutSeconds = 300)
        => affected.RunAsync(baseRef, projectName, testProjectName, timeoutSeconds);

    [McpServerTool(Name = "continuous_testing"), Description(
        "Continuous testing without an IDE: action=start watches the solution's source files and, after a save and a " +
        "short debounce, runs the tests once (all, or testFilter), keeping the last result; stop ends the loop, " +
        "status reports it (runs, last counts, last failures) and is cheap to poll, run-now runs once immediately. " +
        "The change signal is the file's write time, so an editor save counts, not only a tool write.")]
    public static Task<object> ContinuousTesting(
        IContinuousTestingService continuous,
        [Description("'start' (default), 'stop', 'status' or 'run-now'.")] string action = "start",
        [Description("Seconds between change checks (default 10, min 2, max 600).")] int intervalSeconds = 10,
        [Description("Optional: the test project to run.")] string? projectName = null,
        [Description("Optional: a dotnet test --filter expression; omit to run the whole test project.")] string? testFilter = null,
        [Description("For run-now: timeout in seconds (default 600).")] int timeoutSeconds = 600)
    {
        var verb = (action ?? "start").Trim().ToLowerInvariant();
        return verb switch
        {
            "stop" => continuous.StopAsync(),
            "status" => Task.FromResult(continuous.Status()),
            "run-now" => continuous.RunNowAsync(projectName, testFilter, timeoutSeconds),
            _ => continuous.StartAsync(intervalSeconds, projectName, testFilter),
        };
    }

    [McpServerTool(Name = "debug_test"), Description(
        "Debug a unit test: starts VSTest with VSTEST_HOST_DEBUG=1 so its test host waits for a debugger, reads the " +
        "host pid, attaches the DAP adapter, sets the breakpoints and resumes — then it is a normal debug session " +
        "(debug_stack, debug_variables, debug_continue). Pass sessionId alone to read what the test host printed so " +
        "far (the test results arrive there, not on the debug channel). Requires debug_install once. Slow: it builds " +
        "and starts a test host.")]
    public static Task<object> DebugTest(
        IDebugTestService service,
        [Description("Optional: the test project. Omit when the solution has exactly one test project.")] string? projectName = null,
        [Description("Optional: a test name (fragment or fully qualified).")] string? testName = null,
        [Description("Optional: a test class name.")] string? className = null,
        [Description("Optional: a raw dotnet test --filter expression (wins over testName/className).")] string? filter = null,
        [Description("Breakpoints to set before resuming: array of {file, line, condition?, hitCondition?}.")] BreakpointSpec[]? breakpoints = null,
        [Description("Seconds to wait for the test host to announce itself (default 60, max 600).")] int startTimeoutSeconds = 60,
        [Description("After the breakpoints are set, seconds to wait for the first stop (default 15).")] int waitForStopSeconds = 15,
        [Description("A session id from a previous debug_test call: return what that run printed and stop here.")] string? sessionId = null)
        => string.IsNullOrWhiteSpace(sessionId)
            ? service.DebugTestAsync(new DebugTestRequest(
                projectName, testName, className, filter, breakpoints, startTimeoutSeconds, waitForStopSeconds))
            : Task.FromResult(service.Output(sessionId!, 200));
}
