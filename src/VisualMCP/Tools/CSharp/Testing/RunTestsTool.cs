using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class RunTestsTool
{
    [McpServerTool, Description(
        "Run all tests in the loaded solution using 'dotnet test' and return structured results " +
        "(pass/fail/skip counts, duration, and failure details). Requires LoadSolution to have been called first.")]
    public static Task<object> RunTests(
        IRunTestsService svc,
        [Description("Optional: run only projects whose name contains this string (case-insensitive)")] string? projectFilter = null,
        [Description("Optional: extra arguments forwarded verbatim to 'dotnet test' (e.g. '--no-build', '--configuration Release')")] string? extraArgs = null)
        => svc.RunTests(projectFilter, extraArgs);
}
