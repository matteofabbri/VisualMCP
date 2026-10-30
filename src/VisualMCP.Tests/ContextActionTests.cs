using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// One test per context action, plus the refusals that keep the family honest. Each builds a real tiny
/// solution over temp files, runs the service with dryRun: false and asserts on the file that was written.
/// </summary>
public sealed class ContextActionTests
{
    [Fact]
    public async Task ImportType_AddsTheUsingOfAMetadataType()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public StringBuilder Builder { get; } = new StringBuilder();
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("import_type", workspace.PathOf("C.cs"), 5, text: "StringBuilder", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("using System.Text;", text);
        Assert.StartsWith("using System.Text;", text);
    }

    [Fact]
    public async Task ImportType_RefusesWhenTheTypeIsAlreadyInScope()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Text;

            namespace Probe;

            internal class C
            {
                public StringBuilder Builder { get; } = new StringBuilder();
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("import_type", workspace.PathOf("C.cs"), 7, text: "StringBuilder");

        Assert.Contains("already in scope", ResultAssert.Error(result));
    }

    [Fact]
    public async Task CheckParamNull_AddsTheGuardAndTheUsing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run(string value)
                {
                    System.Console.WriteLine(value);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("check_param_null", workspace.PathOf("C.cs"), 5, text: "value", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("if (value is null) throw new ArgumentNullException(nameof(value));", text);
        Assert.Contains("using System;", text);
    }

    [Fact]
    public async Task CheckAllParamsNull_GuardsEveryReferenceTypeParameter()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run(string first, object second)
                {
                    System.Console.WriteLine(first);
                    System.Console.WriteLine(second);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("check_all_params_null", workspace.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("ArgumentNullException(nameof(first))", text);
        Assert.Contains("ArgumentNullException(nameof(second))", text);
    }

    [Fact]
    public async Task CheckStringNotNullOrEmpty_UsesIsNullOrEmpty()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run(string value)
                {
                    System.Console.WriteLine(value);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply(
            "check_string_not_null_or_empty", workspace.PathOf("C.cs"), 5, text: "value", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("if (string.IsNullOrEmpty(value))", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task CheckCollectionNotEmpty_UsesCountWhenTheTypeHasOne()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Collections.Generic;

            namespace Probe;

            internal class C
            {
                public void Run(List<int> items)
                {
                    System.Console.WriteLine(items.Count);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply(
            "check_collection_not_empty", workspace.PathOf("C.cs"), 7, text: "items", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("if (items is null || items.Count == 0)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task CheckVariableNull_InsertsTheGuardAfterTheDeclaration()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    var text = "x";
                    System.Console.WriteLine(text);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("check_variable_null", workspace.PathOf("C.cs"), 7, text: "text", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("if (text is null) throw new InvalidOperationException(", text);
        Assert.True(text.IndexOf("var text = \"x\";", StringComparison.Ordinal) <
                    text.IndexOf("if (text is null)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrapInTryCatch_WrapsTheWholeRangeAndKeepsTheBody()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    System.Console.WriteLine(1);
                    System.Console.WriteLine(2);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("wrap_in_try_catch", workspace.PathOf("C.cs"), 7, toLine: 8, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("try", text);
        Assert.Contains("catch (Exception)", text);
        Assert.Contains("throw;", text);
        Assert.Contains("System.Console.WriteLine(1);", text);
        Assert.Contains("System.Console.WriteLine(2);", text);
        Assert.Contains("using System;", text);
    }

    [Fact]
    public async Task WrapInTryFinally_WrapsTheWholeRange()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    System.Console.WriteLine(1);
                    System.Console.WriteLine(2);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("wrap_in_try_finally", workspace.PathOf("C.cs"), 7, toLine: 8, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("finally", text);
        Assert.Contains("System.Console.WriteLine(2);", text);
    }

    [Fact]
    public async Task WrapInIf_UsesTheConditionFromText()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    System.Console.WriteLine(1);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply(
            "wrap_in_if", workspace.PathOf("C.cs"), 7, toLine: 7, text: "System.DateTime.Now.Year > 2000", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("if (System.DateTime.Now.Year > 2000)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task WrapInUsing_WrapsTheRangeInTheResource()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    var stream = new System.IO.MemoryStream();
                    stream.WriteByte(1);
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("wrap_in_using", workspace.PathOf("C.cs"), 8, toLine: 8, text: "stream", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("using (stream)", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task StringToRaw_KeepsTheValueWithRealNewlines()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string Text = "line1\nline2";
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("string_to_raw", workspace.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("\"\"\"", text);
        Assert.Contains("line1\n    line2", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task StringToVerbatim_DoublesTheQuotes()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string Text = "a\"b";
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("string_to_verbatim", workspace.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("@\"a\"\"b\"", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task StringToRegular_EscapesWhatTheVerbatimStringCouldSpell()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public string Text = @"a'b";
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("string_to_regular", workspace.PathOf("C.cs"), 5, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("Text = \"a'b\"", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task ArgumentGuard_RefusesAnExpressionBodiedMethod()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public int Twice(int value) => value * 2;
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("check_param_null", workspace.PathOf("C.cs"), 5, text: "value");

        Assert.Contains("expression-bodied", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Wrap_RefusesARangeThatCutsAStatementInHalf()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
                public void Run()
                {
                    if (true)
                    {
                        System.Console.WriteLine(1);
                        System.Console.WriteLine(2);
                    }
                }
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("wrap_in_try_catch", workspace.PathOf("C.cs"), 7, toLine: 8);

        Assert.Contains("cuts one in half", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Apply_RefusesAnUnknownActionAndListsTheCatalogue()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            namespace Probe;

            internal class C
            {
            }
            """));

        var service = new ContextActionService(workspace);
        var result = await service.Apply("make_coffee", workspace.PathOf("C.cs"), 3);

        var error = ResultAssert.Error(result);
        Assert.Contains("Unknown context action", error);
        Assert.Contains("import_type", error);
    }
}
