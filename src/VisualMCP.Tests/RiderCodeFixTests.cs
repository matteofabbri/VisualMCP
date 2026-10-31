using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Snitch.Languages.CSharp.Inspections.Rider;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The rider pack's repair half: each rule reports under ReSharper's id, and the fix provider turns
/// the finding into the same edit ReSharper offers.
/// </summary>
public sealed class RiderCodeFixTests
{
    private static async Task<Solution> BuildAsync(string source)
    {
        var workspace = FakeWorkspace.Create(("C.cs", source));
        return await workspace.EnsureSolutionLoadedAsync() ?? throw new InvalidOperationException("no solution");
    }

    private static async Task<string?> FirstIdAsync(string source, DiagnosticAnalyzer analyzer)
    {
        var solution = await BuildAsync(source);
        var compilation = await solution.Projects.Single().GetCompilationAsync();
        var diagnostics = await compilation!
            .WithAnalyzers(ImmutableArray.Create(analyzer))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.Select(diagnostic => diagnostic.Id).FirstOrDefault();
    }

    private static async Task<string> FixAsync(string source, DiagnosticAnalyzer analyzer, string id)
    {
        var solution = await BuildAsync(source);
        var document = solution.Projects.Single().Documents.Single();
        var compilation = await document.Project.GetCompilationAsync();
        var diagnostic = (await compilation!
                .WithAnalyzers(ImmutableArray.Create(analyzer))
                .GetAnalyzerDiagnosticsAsync())
            .FirstOrDefault(candidate => candidate.Id == id);

        Assert.NotNull(diagnostic);

        var actions = new List<CodeAction>();
        await new RiderCodeFixProvider().RegisterCodeFixesAsync(
            new CodeFixContext(document, diagnostic!, (action, _) => actions.Add(action), CancellationToken.None));

        var action = Assert.Single(actions);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var text = await changed.GetDocument(document.Id)!.GetTextAsync();
        return text.ToString();
    }

    [Fact]
    public async Task ConvertToCompoundAssignment_reports_and_rewrites()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M(int x)
                {
                    x = x + 1;
                    return x;
                }
            }
            """;

        Assert.Equal("ConvertToCompoundAssignment", await FirstIdAsync(source, new ConvertToCompoundAssignmentAnalyzer()));
        Assert.Contains("x += 1;", await FixAsync(source, new ConvertToCompoundAssignmentAnalyzer(), "ConvertToCompoundAssignment"));
    }

    [Fact]
    public async Task RedundantLambdaSignatureParentheses_reports_and_rewrites()
    {
        const string source = """
            using System;

            namespace Probe;

            internal class C
            {
                public Func<int, int> M() => (x) => x;
            }
            """;

        Assert.Equal("RedundantLambdaSignatureParentheses",
            await FirstIdAsync(source, new RedundantLambdaSignatureParenthesesAnalyzer()));
        Assert.Contains("x => x", await FixAsync(source, new RedundantLambdaSignatureParenthesesAnalyzer(),
            "RedundantLambdaSignatureParentheses"));
    }

    [Fact]
    public async Task UseNameofExpression_reports_and_rewrites()
    {
        const string source = """
            using System;

            namespace Probe;

            internal class C
            {
                public void M(string value)
                {
                    if (value is null) throw new ArgumentNullException("value");
                }
            }
            """;

        Assert.Equal("UseNameofExpression", await FirstIdAsync(source, new UseNameofExpressionAnalyzer()));
        Assert.Contains("ArgumentNullException(nameof(value))",
            await FixAsync(source, new UseNameofExpressionAnalyzer(), "UseNameofExpression"));
    }

    [Fact]
    public async Task StringLiteralAsInterpolationArgument_reports_and_rewrites()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public string M(string a) => $"{a}{"b"}";
            }
            """;

        Assert.Equal("StringLiteralAsInterpolationArgument",
            await FirstIdAsync(source, new StringLiteralAsInterpolationArgumentAnalyzer()));
        Assert.Contains("$\"{a}b\"", await FixAsync(source, new StringLiteralAsInterpolationArgumentAnalyzer(),
            "StringLiteralAsInterpolationArgument"));
    }

    [Fact]
    public async Task RedundantCheckBeforeAssignment_reports_and_rewrites()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public void M(int a, int b)
                {
                    if (a != b) a = b;
                }
            }
            """;

        Assert.Equal("RedundantCheckBeforeAssignment",
            await FirstIdAsync(source, new RedundantCheckBeforeAssignmentAnalyzer()));

        var fixedText = await FixAsync(source, new RedundantCheckBeforeAssignmentAnalyzer(), "RedundantCheckBeforeAssignment");
        Assert.DoesNotContain("if (", fixedText);
        Assert.Contains("a = b;", fixedText);
    }

    [Fact]
    public async Task ConvertToConstant_adds_const()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    int limit = 10;
                    return limit;
                }
            }
            """;

        Assert.Contains("const int limit = 10;",
            await FixAsync(source, new ConvertToConstantLocalAnalyzer(), "ConvertToConstant_Local"));
    }

    [Fact]
    public async Task MemberCanBeMadeStatic_adds_static()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public int M()
                {
                    return 1;
                }
            }
            """;

        Assert.Contains("public static int M()",
            await FixAsync(source, new MemberCanBeMadeStaticAnalyzer(), "MemberCanBeMadeStatic_Global"));
    }

    [Fact]
    public async Task EmptyConstructor_removes_the_constructor()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public C()
                {
                }
            }
            """;

        var fixedText = await FixAsync(source, new EmptyConstructorAnalyzer(), "EmptyConstructor");
        Assert.DoesNotContain("public C()", fixedText);
    }

    [Fact]
    public async Task RedundantBaseConstructorCall_removes_the_initializer()
    {
        const string source = """
            namespace Probe;

            internal class B
            {
                public B()
                {
                }
            }

            internal class C : B
            {
                public C() : base()
                {
                }
            }
            """;

        var fixedText = await FixAsync(source, new RedundantBaseConstructorCallAnalyzer(), "RedundantBaseConstructorCall");
        Assert.DoesNotContain("base()", fixedText);
    }

    [Fact]
    public async Task RedundantIfElseBlock_returns_the_condition()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                public bool M(int x)
                {
                    if (x > 0) return true; else return false;
                }
            }
            """;

        var fixedText = await FixAsync(source, new RedundantIfElseAnalyzer(), "RedundantIfElseBlock");
        Assert.Contains("return x > 0;", fixedText);
        Assert.DoesNotContain("if (", fixedText);
    }

    [Fact]
    public async Task RedundantArgument_removes_the_argument()
    {
        const string source = """
            namespace Probe;

            internal class C
            {
                private void M(int x = 5)
                {
                }

                public void N() => M(5);
            }
            """;

        var fixedText = await FixAsync(source, new RedundantArgumentAnalyzer(), "RedundantArgument");
        Assert.Contains("M();", fixedText);
    }
}
