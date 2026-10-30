using Snitch.Languages.CSharp.Analysis;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// fix_diagnostics rewrites the compiler's own mechanical diagnostics — the family
/// <c>apply_code_fix</c> cannot reach, because a <c>CS####</c> ships no CodeFixProvider.
/// Each test asserts the text on disk after a written run, and the gate is exercised by the
/// "nothing breaks" case: a fix that would add a compiler error is refused, not written.
/// </summary>
public sealed class FixDiagnosticsTests
{
    private static FixDiagnosticsService Service(FakeWorkspace workspace) => new(workspace);

    [Fact]
    public async Task An_unused_local_initialized_with_a_constant_is_removed()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                public void Run()
                {
                    int value = 1 + 2;
                }
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.DoesNotContain("int value", text);
        Assert.Equal(1, ResultAssert.Json(result).GetProperty("fixed").GetInt32());
    }

    [Fact]
    public async Task A_local_initialized_by_a_call_is_not_a_compiler_diagnostic()
    {
        // The compiler raises CS0219 only for a constant initializer: a call may have a side effect,
        // so it stays silent. This locks the fact the service is built on.
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                public void Run()
                {
                    var value = Compute();
                }

                private static int Compute() => 42;
            }
            """));

        var result = await Service(workspace).FixAsync();
        ResultAssert.Succeeded(result);

        Assert.Equal(0, ResultAssert.Json(result).GetProperty("found").GetInt32());
    }

    [Fact]
    public async Task An_unused_local_without_an_initializer_is_removed()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                public void Run()
                {
                    int unused;
                }
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.DoesNotContain("int unused;", workspace.Read("Probe.cs"));
    }

    [Fact]
    public async Task An_unused_private_field_is_removed()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                private int _unused;

                public int Value => 1;
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.DoesNotContain("_unused", text);
        Assert.Contains("public int Value => 1;", text);
    }

    [Fact]
    public async Task A_member_that_hides_an_inherited_one_is_marked_new()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Base
            {
                public void Go() { }
            }

            public class Derived : Base
            {
                public void Go() { }
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("public new void Go()", workspace.Read("Probe.cs"));
    }

    [Fact]
    public async Task A_new_modifier_that_hides_nothing_is_removed()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                public new void Go() { }
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.Contains("public void Go()", text);
        Assert.DoesNotContain("new void Go", text);
    }

    [Fact]
    public async Task A_duplicated_using_is_removed()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            using System;
            using System;

            public class Probe
            {
                public string Empty => string.Empty;
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        var occurrences = workspace.Read("Probe.cs").Split("using System;").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task A_preview_writes_nothing_and_returns_the_diff()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                private int _unused;
            }
            """));

        var before = workspace.Read("Probe.cs");
        var result = await Service(workspace).FixAsync();
        ResultAssert.Succeeded(result);

        Assert.Equal(before, workspace.Read("Probe.cs"));

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, json.GetProperty("changedFiles").GetInt32());

        var file = Assert.Single(json.GetProperty("files").EnumerateArray());
        Assert.Contains("_unused", file.GetProperty("preview").GetString());
    }

    [Fact]
    public async Task An_unsupported_id_is_refused_with_the_supported_list()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "public class Probe { }"));

        var result = await Service(workspace).FixAsync(diagnosticIds: "CS0246");

        Assert.Contains("no automatic fix", ResultAssert.Error(result));
        Assert.NotEmpty(ResultAssert.Json(result).GetProperty("supported").EnumerateArray());
    }

    [Fact]
    public async Task A_clean_file_reports_nothing_to_do()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", """
            public class Probe
            {
                public int Value => 1;
            }
            """));

        var result = await Service(workspace).FixAsync(dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("found").GetInt32());
        Assert.Equal(0, json.GetProperty("fixed").GetInt32());
    }
}
