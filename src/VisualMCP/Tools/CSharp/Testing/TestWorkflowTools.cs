using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class TestWorkflowTools
{
    [McpServerTool(Name = "run_test"), Description(
        "Run ONE test or one test class without hand-building `dotnet test --filter`: pass testName (a fully " +
        "qualified name, or a fragment) or className, or a raw filter. The result is structured (TRX): passed, " +
        "failed, the failed test names and their messages — and the failures are RECORDED, so rerun_failed " +
        "re-runs exactly them. Resolves the test project automatically when the solution has one.")]
    public static Task<object> RunTest(
        ITestWorkflowService tests,
        [Description("Fully-qualified test name (or a fragment, matched with ~).")] string? testName = null,
        [Description("Test class name (all its tests).")] string? className = null,
        [Description("The test project, when the solution has several.")] string? projectName = null,
        [Description("A raw --filter expression, used as-is (wins over testName/className).")] string? filter = null,
        [Description("Timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => tests.RunTestAsync(testName, className, projectName, filter, timeoutSeconds);

    [McpServerTool(Name = "rerun_failed"), Description(
        "Re-run exactly the tests that failed in the last run_test or run_tests call, from the recorded " +
        "failures in .visualMCP/last-test-run.json. No argument to remember: the record is written by the " +
        "previous run and cleared by a green one. Each project re-runs its own failures unless projectName " +
        "names one. Returns the same structured result and updates the record.")]
    public static Task<object> RerunFailed(
        ITestWorkflowService tests,
        [Description("The test project, when it changed or the record is ambiguous.")] string? projectName = null,
        [Description("Timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => tests.RerunFailedAsync(projectName, timeoutSeconds);

    [McpServerTool(Name = "tests_covering_symbol"), Description(
        "Which test methods reach this symbol: Roslyn finds every reference, keeps the ones inside a method " +
        "marked [Fact]/[Theory]/[Test]/[TestCase]/[DataTestMethod], and returns the test names with file:line. " +
        "This is the semantic answer to 'which test should I run' — direct references only: a test that " +
        "reaches the symbol through another method or reflection is not traced (get_test_coverage_map says " +
        "whether the lines are covered at all). Read-only.")]
    public static Task<object> TestsCoveringSymbol(
        ITestWorkflowService tests,
        [Description("The symbol name (method, property, type).")] string symbolName,
        [Description("Containing type, when the name is ambiguous.")] string? containingType = null,
        [Description("Maximum tests to return (default 200).")] int maxResults = 200)
        => tests.TestsCoveringSymbolAsync(symbolName, containingType, maxResults);

    [McpServerTool(Name = "generate_tests"), Description(
        "Scaffold an xUnit test class for a type: one [Fact] per public instance method, with Arrange/Act/Assert " +
        "bodies that compile (default! arguments, `new T()` when there is a public parameterless constructor), " +
        "written into the test project. Compiler-gated: if the generated class would not compile (the project " +
        "does not reference xunit, or a name clashes) nothing is written and the errors are reported. Previewed " +
        "unless dryRun=false. The bodies assert nothing yet — they mark what to test.")]
    public static Task<object> GenerateTests(
        ITestWorkflowService tests,
        [Description("The type to test (simple or fully-qualified name).")] string typeName,
        [Description("The project that declares the type, when the name is ambiguous.")] string? projectName = null,
        [Description("The test project to write into (default: the solution's single test project).")] string? testProjectName = null,
        [Description("Class name, when the default <Type>Tests is taken.")] string? className = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => tests.GenerateTestsAsync(typeName, projectName, testProjectName, className, dryRun);

    [McpServerTool(Name = "test_impact_analysis"), Description(
        "Which tests a change can affect: git diff against baseRef (default HEAD) names the changed .cs files, " +
        "the symbols those files declare are looked up, and the test methods that reference them are returned " +
        "with a ready run_test filter. It names them and prints the command; it does not run them. Direct " +
        "references only — a test that reaches the change indirectly is not listed.")]
    public static Task<object> TestImpactAnalysis(
        ITestWorkflowService tests,
        [Description("The git ref to compare against (default HEAD).")] string? baseRef = null,
        [Description("Only this project (exact name).")] string? projectName = null,
        [Description("Maximum tests to return (default 200).")] int maxResults = 200)
        => tests.ImpactAnalysisAsync(baseRef, projectName, maxResults);
}
