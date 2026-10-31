using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class VerifyChangeTool
{
    [McpServerTool(Name = "verify_change"), Description(
        "Did this change alter behaviour? Runs the test suite on the working tree AND on a git revision " +
        "checked out in a detached worktree, then diffs the outcome test by test: regressions (passed " +
        "before, not now), fixes, new tests, removed tests, still-failing ones, and a verdict — " +
        "behaviour-preserved, regression, nothing-to-verify when the tree matches the revision, or " +
        "unverified when the filter ran no test. This is the runtime half of analysis: the compiler gate " +
        "proves the code builds, this measures what it does. Slow by design (two builds and two runs): " +
        "narrow it with testFilter / projectName, or pass includeBaseline=false for the working tree only.")]
    public static Task<object> VerifyChange(
        IVerifyChangeService service,
        [Description("The revision to compare against (default HEAD): a branch, tag or commit hash.")] string? baseRef = null,
        [Description("The test project whose suite runs. Omit when the solution has exactly one test project.")] string? projectName = null,
        [Description("A dotnet test --filter expression to narrow the run (e.g. 'FullyQualifiedName~OrderService').")] string? testFilter = null,
        [Description("Run the baseline revision too (default true). false reports the working tree only.")] bool includeBaseline = true,
        [Description("Timeout per run in seconds (default 900, max 3600).")] int timeoutSeconds = 900)
        => service.VerifyAsync(baseRef, projectName, testFilter, includeBaseline, timeoutSeconds);
}
