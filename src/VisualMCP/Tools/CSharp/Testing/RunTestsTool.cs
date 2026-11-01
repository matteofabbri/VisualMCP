using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class RunTestsTool
{
    [McpServerTool, Description(
        "Run the solution's tests with 'dotnet test' and return structured results (pass/fail/skip counts, " +
        "duration, per-project results and the failing tests with their message and stack trace). The failed " +
        "tests are recorded, so rerun_failed re-runs exactly them. The working-directory solution auto-loads " +
        "on first use.")]
    public static Task<object> RunTests(
        IRunTestsService svc,
        [Description("Optional: run only the test projects whose name (or file name) contains this string, case-insensitive. Omit to run the whole solution.")] string? projectFilter = null,
        [Description("Optional: extra arguments forwarded verbatim to 'dotnet test' (e.g. '--no-build', '--configuration Release')")] string? extraArgs = null)
        => svc.RunTests(projectFilter, extraArgs);
}
