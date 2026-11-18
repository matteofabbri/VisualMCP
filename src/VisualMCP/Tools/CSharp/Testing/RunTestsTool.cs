using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class RunTestsTool
{
    [McpServerTool, Description(
        "Run the solution's tests with 'dotnet test' and return structured results (pass/fail/skip counts, " +
        "duration, per-project results and the failing tests with their message and stack trace). This is the " +
        "full-suite gate: to run only the tests a change reaches call run_affected_tests, to repeat a failure " +
        "call rerun_failed, and for one test or class call run_test. The failed tests are recorded, so " +
        "rerun_failed re-runs exactly them. The whole call is bounded by timeoutSeconds; when it expires the " +
        "'dotnet test' process tree is killed and the result says timedOut. The working-directory solution " +
        "auto-loads on first use.")]
    public static Task<object> RunTests(
        IRunTestsService svc,
        [Description("Optional: run only the test projects whose name (or file name) contains this string, case-insensitive. Omit to run the whole solution.")] string? projectFilter = null,
        [Description("Optional: extra arguments forwarded verbatim to 'dotnet test' (e.g. '--no-build', '--configuration Release')")] string? extraArgs = null,
        [Description("Timeout for the whole call in seconds (default 600), shared by every selected target; on expiry the 'dotnet test' process tree is killed.")] int timeoutSeconds = 600)
        => svc.RunTests(projectFilter, extraArgs, timeoutSeconds);
}
