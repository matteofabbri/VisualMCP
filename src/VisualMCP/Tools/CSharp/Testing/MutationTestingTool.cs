using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Testing;

namespace VisualMCP.Tools.CSharp.Testing;

[McpServerToolType]
public static class MutationTestingTool
{
    [McpServerTool(Name = "run_mutation_tests"), Description(
        "Run Stryker.NET over a test project and report what the suite did not catch: the mutation score " +
        "(detected / tested), killed, survived, timed out, no-coverage, ignored and compile-error mutant " +
        "counts, the worst files and the first surviving mutants with their file and line. A survivor is a " +
        "mutation the tests did not notice — the strongest quality signal a suite can give. Requires " +
        "'dotnet tool install -g dotnet-stryker' (the error says so when it is missing). Long run: budget " +
        "minutes, not seconds.")]
    public static Task<object> RunMutationTests(
        IMutationTestingService mutation,
        [Description("The test project whose tests run the mutation. Omit when the solution has exactly one test project.")] string? projectName = null,
        [Description("Timeout in seconds for the whole Stryker run (default 1800, max 3600).")] int timeoutSeconds = 1800,
        [Description("Optional raw Stryker arguments, e.g. '--mutate \"src/**/*.cs\" --since:main'.")] string? extraArgs = null)
        => mutation.RunAsync(projectName, timeoutSeconds, extraArgs);
}
