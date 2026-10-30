using Snitch.Languages.CSharp.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// <c>simplify_code</c> answers IDE0005, IDE0032 and IDE0290 by delegating to their own engine services,
/// turning a scope request into the candidate list those services need. The delegated work keeps its own
/// compiler gate and its own edit-history record; this test only proves the composition reaches it.
/// </summary>
public sealed class SimplifyCodeDelegationTests
{
    [Fact]
    public async Task IDE0032_finds_the_property_in_the_scope_and_rewrites_it()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _value;

                public int Value
                {
                    get => _value;
                    set => _value = value;
                }
            }
            """));

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0032", dryRun: false);

        var json = ResultAssert.Json(result);
        var delegated = json.GetProperty("delegated")[0];
        Assert.Equal("property_to_auto", delegated.GetProperty("action").GetString());
        Assert.Equal(1, delegated.GetProperty("candidates").GetInt32());

        var text = workspace.Read("C.cs");
        Assert.Contains("public int Value { get; set; }", text, StringComparison.Ordinal);
        Assert.DoesNotContain("get => _value;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IDE0290_finds_the_type_and_converts_its_constructor()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private readonly int _value;

                public C(int value)
                {
                    _value = value;
                }

                public int Value => _value;
            }
            """));

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0290", dryRun: false);

        var delegated = ResultAssert.Json(result).GetProperty("delegated")[0];
        Assert.Equal("convert_to_primary_constructor", delegated.GetProperty("action").GetString());
        Assert.Equal(1, delegated.GetProperty("candidates").GetInt32());

        Assert.Contains("internal class C(int value)", workspace.Read("C.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IDE0005_removes_the_unused_using()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Text;

            internal class C
            {
                public int F() => 1;
            }
            """));

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0005", dryRun: false);

        var delegated = ResultAssert.Json(result).GetProperty("delegated")[0];
        Assert.Equal("optimize_usings", delegated.GetProperty("action").GetString());
        Assert.DoesNotContain("using System.Text;", workspace.Read("C.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mixed_request_runs_the_style_half_and_the_delegated_half()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _value;

                public int Value
                {
                    get => _value;
                    set => _value = value;
                }

                public bool F(object? input) => input == null;
            }
            """));

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0032,IDE0041", dryRun: false);

        var json = ResultAssert.Json(result);

        // The style half reports as it always does.
        Assert.Equal(1, json.GetProperty("style").GetProperty("filesChanged").GetInt32());
        Assert.Contains("is null", workspace.Read("C.cs"), StringComparison.Ordinal);

        // The delegated half is one entry per id.
        Assert.Equal("IDE0032", json.GetProperty("delegated")[0].GetProperty("id").GetString());
        Assert.Contains("public int Value { get; set; }", workspace.Read("C.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_rules_does_not_pull_in_the_delegated_ids()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _value;

                public int Value
                {
                    get => _value;
                    set => _value = value;
                }
            }
            """));

        var result = await new SimplifyCodeService(workspace)
            .SimplifyAsync("document", workspace.PathOf("C.cs"), null, rules: null, dryRun: true);

        var json = ResultAssert.Json(result);
        Assert.False(json.TryGetProperty("delegated", out _));
        Assert.DoesNotContain("IDE0032", json.GetProperty("rules").ToString(), StringComparison.Ordinal);
    }
}
