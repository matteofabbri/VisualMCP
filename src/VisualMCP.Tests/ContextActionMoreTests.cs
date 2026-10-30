using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The context-action families added after the first 13: braces, region, foreach→for, concatenation
/// → interpolation and the index guard — each previewed and compiler-gated like the others.
/// </summary>
public sealed class ContextActionMoreTests
{
    private const string Source = """
        using System.Collections.Generic;

        internal class C
        {
            public void M(int[] xs, bool flag, string name, int index)
            {
                if (flag) System.Console.WriteLine(name);
                foreach (var x in xs) System.Console.WriteLine(x);
                var s = "a" + name;
                var v = xs[index];
            }
        }
        """;

    private static async Task<(FakeWorkspace Workspace, ContextActionService Service)> ApplyAsync(
        string action, int line, string? text = null, int? toLine = null)
    {
        var workspace = FakeWorkspace.Create(("C.cs", Source));
        var service = new ContextActionService(workspace);
        var result = await service.Apply(action, workspace.PathOf("C.cs"), line, toLine, text, dryRun: false);
        ResultAssert.Succeeded(result);
        return (workspace, service);
    }

    [Fact]
    public async Task AddBraces_WrapsTheEmbeddedStatement()
    {
        var (workspace, _) = await ApplyAsync("add_braces", 7);
        using var scope = workspace;

        var text = workspace.Read("C.cs");
        Assert.Contains("if (flag)", text);
        Assert.Contains("{", text);
        Assert.Contains("System.Console.WriteLine(name);", text);
        Assert.Contains("}", text);
    }

    [Fact]
    public async Task WrapInRegion_AddsTheDirectivesAroundTheRange()
    {
        var (workspace, _) = await ApplyAsync("wrap_in_region", 8, "loop", 9);
        using var scope = workspace;

        var lines = File.ReadAllLines(workspace.PathOf("C.cs"));
        var start = Array.FindIndex(lines, line => line.Trim() == "#region loop");
        var end = Array.FindIndex(lines, line => line.Trim() == "#endregion");
        var loop = Array.FindIndex(lines, line => line.Contains("foreach (var x in xs)"));

        Assert.True(start >= 0 && start < loop);
        Assert.True(end > loop);
    }

    [Fact]
    public async Task ForeachToFor_RebuildsTheLoopOverAnArray()
    {
        var (workspace, _) = await ApplyAsync("foreach_to_for", 8);
        using var scope = workspace;

        var text = workspace.Read("C.cs");
        Assert.Contains("for (var i = 0; i < xs.Length; i++)", text);
        Assert.Contains("var x = xs[i];", text);
    }

    [Fact]
    public async Task ConcatToInterpolated_RewritesTheConcatenation()
    {
        var (workspace, _) = await ApplyAsync("concat_to_interpolated", 9);
        using var scope = workspace;

        Assert.Contains("var s = $\"a{name}\";", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task CheckIndexRange_InsertsTheGuardBeforeTheStatement()
    {
        var (workspace, _) = await ApplyAsync("check_index_range", 10, "index");
        using var scope = workspace;

        var text = workspace.Read("C.cs");
        Assert.Contains("if (index < 0 || index >= xs.Length)", text);
        Assert.Contains("throw new ArgumentOutOfRangeException(nameof(index));", text);

        var guard = text.IndexOf("ArgumentOutOfRangeException", StringComparison.Ordinal);
        var use = text.IndexOf("xs[index]", StringComparison.Ordinal);
        Assert.True(guard < use, "the guard must come before the element access");
    }

    [Fact]
    public async Task NewActions_AreListedAndUnknownOnesAreRefused()
    {
        var workspace = FakeWorkspace.Create(("C.cs", Source));
        using var scope = workspace;
        var service = new ContextActionService(workspace);

        foreach (var id in new[] { "add_braces", "wrap_in_region", "foreach_to_for", "concat_to_interpolated", "check_index_range" })
            Assert.Contains(ContextActionService.Catalogue, entry => entry.Id == id);

        Assert.Contains("Unknown context action",
            ResultAssert.Error(await service.Apply("explode", workspace.PathOf("C.cs"), 7)));
    }
}
