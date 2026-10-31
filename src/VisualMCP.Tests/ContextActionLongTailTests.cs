using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The long-tail context actions: the two directions of a nested check, a null check joined with its
/// use, var and back, a split declaration, brace removal, nullable migration, digit separators and
/// ConfigureAwait — each a verified splice behind the compiler gate.
/// </summary>
public sealed class ContextActionLongTailTests
{
    private static async Task<FakeWorkspace> ApplyAsync(string source, string action, int line, string? text = null)
    {
        var workspace = FakeWorkspace.Create(("C.cs", source));
        var service = new ContextActionService(workspace);
        var result = await service.Apply(action, workspace.PathOf("C.cs"), line, null, text, dryRun: false);
        ResultAssert.Succeeded(result);
        return workspace;
    }

    [Fact]
    public async Task MergeSequentialChecks_CombinesTheConditions()
    {
        const string source = """
            internal class C
            {
                public void M(bool a, bool b)
                {
                    if (a)
                    {
                        if (b)
                        {
                            System.Console.WriteLine("both");
                        }
                    }
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "merge_sequential_checks", 5);

        var text = workspace.Read("C.cs");
        Assert.Contains("if (a && b)", text);
        Assert.Equal(1, text.Split("if (").Length - 1);
        Assert.Contains("System.Console.WriteLine(\"both\");", text);
    }

    [Fact]
    public async Task SplitAndInIf_SplitsTheCondition()
    {
        const string source = """
            internal class C
            {
                public void M(bool a, bool b)
                {
                    if (a && b)
                    {
                        System.Console.WriteLine("both");
                    }
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "split_and_in_if", 5);

        var text = workspace.Read("C.cs");
        Assert.Contains("if (a)", text);
        Assert.Contains("if (b)", text);
        Assert.Contains("System.Console.WriteLine(\"both\");", text);
    }

    [Fact]
    public async Task JoinNullCheckWithUsage_TurnsTheIfIntoAConditionalAccess()
    {
        const string source = """
            internal class C
            {
                public void M(C? other)
                {
                    if (other != null)
                    {
                        other.ToString();
                    }
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "join_null_check_with_usage", 5);

        var text = workspace.Read("C.cs");
        Assert.Contains("other?.ToString();", text);
        Assert.DoesNotContain("if (other != null)", text);
    }

    [Fact]
    public async Task JoinNullCheck_RefusesABodyThatDoesNotStartWithTheReceiver()
    {
        const string source = """
            internal class C
            {
                public void M(C? other)
                {
                    if (other != null)
                    {
                        System.Console.WriteLine("x");
                    }
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var service = new ContextActionService(workspace);

        var result = await service.Apply("join_null_check_with_usage", workspace.PathOf("C.cs"), 5);
        Assert.Contains("does not start a member access", ResultAssert.Error(result));
    }

    [Fact]
    public async Task UseVar_ReplacesTheExplicitType()
    {
        const string source = """
            internal class C
            {
                public void M()
                {
                    int count = 0;
                    System.Console.WriteLine(count);
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "use_var", 5);
        Assert.Contains("        var count = 0;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task UseExplicitType_NamesTheInferredType()
    {
        const string source = """
            internal class C
            {
                public void M()
                {
                    var count = 0;
                    System.Console.WriteLine(count);
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "use_explicit_type", 5);
        Assert.Contains("        int count = 0;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SplitDeclaration_ProducesOneLinePerDeclarator()
    {
        const string source = """
            internal class C
            {
                public void M()
                {
                    int first = 1, second = 2;
                    System.Console.WriteLine(first + second);
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "split_declaration", 5);

        var text = workspace.Read("C.cs");
        Assert.Contains("        int first = 1;", text);
        Assert.Contains("        int second = 2;", text);
        Assert.DoesNotContain("first = 1, second = 2", text);
    }

    [Fact]
    public async Task RemoveBraces_UnwrapsASingleStatement()
    {
        const string source = """
            internal class C
            {
                public void M(bool flag)
                {
                    if (flag)
                    {
                        System.Console.WriteLine("x");
                    }
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "remove_braces", 6);

        var text = workspace.Read("C.cs");
        Assert.Contains("if (flag)", text);
        Assert.Contains("            System.Console.WriteLine(\"x\");", text);
        Assert.DoesNotContain("if (flag)\n        {", text);
    }

    [Fact]
    public async Task MigrateToNullable_EnablesTheFile()
    {
        const string source = """
            internal class C
            {
                public void M()
                {
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "migrate_to_nullable", 1);
        Assert.StartsWith("#nullable enable", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task MigrateToNullable_TurnsADisableIntoAnEnable()
    {
        const string source = """
            #nullable disable
            internal class C
            {
            }
            """;

        using var workspace = await ApplyAsync(source, "migrate_to_nullable", 1);

        var text = workspace.Read("C.cs");
        Assert.StartsWith("#nullable enable", text);
        Assert.DoesNotContain("#nullable disable", text);
    }

    [Fact]
    public async Task RemoveDigitSeparators_CompactsTheLiteral()
    {
        const string source = """
            internal class C
            {
                public long M() => 1_000_000;
            }
            """;

        using var workspace = await ApplyAsync(source, "remove_digit_separators", 3);
        Assert.Contains("1000000", workspace.Read("C.cs"));
        Assert.DoesNotContain("1_000_000", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task UseConfigureAwaitFalse_AppendsTheCall()
    {
        const string source = """
            internal class C
            {
                public async System.Threading.Tasks.Task M(System.Threading.Tasks.Task work)
                {
                    await work;
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "use_configure_await_false", 5);
        Assert.Contains("await work.ConfigureAwait(false);", workspace.Read("C.cs"));
    }

    [Fact]
    public void TheCatalogueListsTheLongTail()
    {
        foreach (var id in new[]
                 {
                     "merge_sequential_checks", "split_and_in_if", "join_null_check_with_usage", "use_var",
                     "use_explicit_type", "split_declaration", "remove_braces", "migrate_to_nullable",
                     "remove_digit_separators", "use_configure_await_false",
                 })
            Assert.Contains(ContextActionService.Catalogue, entry => entry.Id == id);
    }
}
