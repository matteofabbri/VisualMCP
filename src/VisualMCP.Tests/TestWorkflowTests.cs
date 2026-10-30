using Snitch.Languages.CSharp.Testing;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The test workflow: which tests reach a symbol (semantic, not coverage guessing), and a generated
/// test class that passes the compiler gate. Running dotnet test is covered by the live suite itself
/// and by the integration paths of the other tests.
/// </summary>
public sealed class TestWorkflowTests
{
    private const string Calculator = """
        namespace Demo;

        public sealed class Calculator
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    private const string CalculatorTests = """
        namespace Demo.Tests;

        public sealed class CalculatorTests
        {
            [Fact]
            public void Add_ReturnsTheSum()
            {
                var sut = new Demo.Calculator();
                var result = sut.Add(1, 2);
                if (result != 3) throw new System.Exception("expected 3");
            }
        }
        """;

    [Fact]
    public async Task TestsCoveringSymbol_FindsTheTestThatReferencesTheMethod()
    {
        var workspace = FakeWorkspace.Create(("Calculator.cs", Calculator), ("CalculatorTests.cs", CalculatorTests));
        using var scope = workspace;
        var service = new TestWorkflowService(workspace);

        var result = ResultAssert.Json(await service.TestsCoveringSymbolAsync("Add", "Calculator", 20));
        Assert.Equal(1, result.GetProperty("count").GetInt32());

        var test = result.GetProperty("coveringTests")[0];
        Assert.Contains("Add_ReturnsTheSum", test.GetProperty("test").GetString());
        Assert.EndsWith("CalculatorTests.cs", test.GetProperty("file").GetString());
    }

    [Fact]
    public async Task TestsCoveringSymbol_RefusesAnAmbiguousName()
    {
        const string Second = """
            namespace Demo;

            public sealed class OtherCalculator
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        var workspace = FakeWorkspace.Create(
            ("Calculator.cs", Calculator), ("OtherCalculator.cs", Second), ("CalculatorTests.cs", CalculatorTests));
        using var scope = workspace;
        var service = new TestWorkflowService(workspace);

        var result = ResultAssert.Json(await service.TestsCoveringSymbolAsync("Add", null, 20));
        Assert.Contains("Ambiguous", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GenerateTests_WritesACompilingClassForEveryPublicMethod()
    {
        var references = Directory.GetFiles(AppContext.BaseDirectory, "xunit*.dll", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(references);

        var workspace = FakeWorkspace.CreateWithReferences(references, ("Calculator.cs", Calculator));
        using var scope = workspace;
        var service = new TestWorkflowService(workspace);

        var result = ResultAssert.Json(await service.GenerateTestsAsync("Calculator", null, "Probe", null, dryRun: false));
        Assert.Equal("CalculatorTests", result.GetProperty("generated").GetString());
        Assert.Equal(1, result.GetProperty("tests").GetInt32());
        Assert.True(result.GetProperty("applied").GetBoolean());

        var path = result.GetProperty("file").GetString()!;
        Assert.True(File.Exists(path));

        var source = File.ReadAllText(path);
        Assert.Contains("using Xunit;", source);
        Assert.Contains("public sealed class CalculatorTests", source);
        Assert.Contains("[Fact]", source);
        Assert.Contains("var sut = new Calculator();", source);
        Assert.Contains("var result = sut.Add(default!, default!);", source);
        Assert.Contains("Assert.NotEqual(default, result);", source);
    }

    [Fact]
    public async Task GenerateTests_RefusesWhenTheTestProjectHasNoTestFramework()
    {
        // No xunit reference: the generated `using Xunit;` cannot compile, so the gate must refuse.
        var workspace = FakeWorkspace.Create(("Calculator.cs", Calculator));
        using var scope = workspace;
        var service = new TestWorkflowService(workspace);

        var result = ResultAssert.Json(await service.GenerateTestsAsync("Calculator", null, "Probe", null, dryRun: true));
        Assert.Contains("Refused", result.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "CalculatorTests.cs")));
    }

    [Fact]
    public async Task RunTest_RequiresSomethingToRun()
    {
        var workspace = FakeWorkspace.Create(("Calculator.cs", Calculator));
        using var scope = workspace;
        var service = new TestWorkflowService(workspace);

        Assert.Contains("Pass testName", ResultAssert.Error(await service.RunTestAsync(null, null, null, null, 60)));
    }
}
