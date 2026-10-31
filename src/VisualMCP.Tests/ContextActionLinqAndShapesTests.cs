using Snitch.Languages.CSharp.ContextActions;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The last context-action family: pattern merging, string concatenation to a StringBuilder, LINQ in
/// both directions and the C# 14 extension block. Every case is written so that the compiler gate has
/// to accept the result — a translation that did not compile would fail the test, not pass it.
/// </summary>
public sealed class ContextActionLinqAndShapesTests
{
    private static async Task<FakeWorkspace> ApplyAsync(string source, string action, int line)
    {
        var workspace = FakeWorkspace.Create(("C.cs", source));
        var service = new ContextActionService(workspace);
        var result = await service.Apply(action, workspace.PathOf("C.cs"), line, null, null, dryRun: false);
        ResultAssert.Succeeded(result);
        return workspace;
    }

    private static async Task<string> RefusalAsync(string source, string action, int line)
    {
        using var workspace = FakeWorkspace.Create(("C.cs", source));
        var service = new ContextActionService(workspace);
        var result = await service.Apply(action, workspace.PathOf("C.cs"), line, null, null, dryRun: false);
        return ResultAssert.Error(result);
    }

    [Fact]
    public async Task MergePatterns_TurnsTwoIsChecksIntoOneOrPattern()
    {
        const string source = """
            internal class C
            {
                public bool M(object o)
                {
                    return o is string || o is int;
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "merge_patterns", 5);

        Assert.Contains("return o is string or int;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task MergePatterns_RefusesDifferentReceivers()
    {
        const string source = """
            internal class C
            {
                public bool M(object a, object b)
                {
                    return a is string || b is int;
                }
            }
            """;

        var error = await RefusalAsync(source, "merge_patterns", 5);
        Assert.Contains("different expressions", error);
    }

    [Fact]
    public async Task MergePatterns_DoesNotFoldAnOrGroupIntoAnAnd()
    {
        const string source = """
            internal class C
            {
                public bool M(object o, bool flag)
                {
                    return flag && (o is string || o is int);
                }
            }
            """;

        var error = await RefusalAsync(source, "merge_patterns", 5);
        Assert.Contains("is' pattern", error);
    }

    [Fact]
    public async Task ConcatToStringBuilder_RewritesTheDeclarationTheAppendsAndTheRead()
    {
        const string source = """
            internal class C
            {
                public string M(string[] items)
                {
                    string text = "";
                    foreach (var item in items)
                    {
                        text += item;
                    }

                    return text;
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "concat_to_stringbuilder", 5);

        var text = workspace.Read("C.cs");
        Assert.Contains("var text = new System.Text.StringBuilder();", text);
        Assert.Contains("text.Append(item);", text);
        Assert.Contains("return text.ToString();", text);
        Assert.DoesNotContain("text += item", text);
    }

    [Fact]
    public async Task ConcatToStringBuilder_RefusesAVariableThatIsNeverAppended()
    {
        const string source = """
            internal class C
            {
                public string M()
                {
                    string text = "";
                    return text;
                }
            }
            """;

        var error = await RefusalAsync(source, "concat_to_stringbuilder", 5);
        Assert.Contains("never appended", error);
    }

    [Fact]
    public async Task LinqQueryToMethod_TranslatesFromWhereOrderBySelect()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            internal class C
            {
                public IEnumerable<string> M(IEnumerable<Order> orders)
                    => from o in orders where o.Total > 10 orderby o.Id descending select o.Name;
            }

            internal class Order { public int Id; public int Total; public string Name = ""; }
            """;

        using var workspace = await ApplyAsync(source, "linq_query_to_method", 7);

        var text = workspace.Read("C.cs");
        Assert.Contains(".Where(o => o.Total > 10)", text);
        Assert.Contains(".OrderByDescending(o => o.Id)", text);
        Assert.Contains(".Select(o => o.Name)", text);
        Assert.DoesNotContain("from o in orders", text);
    }

    [Fact]
    public async Task LinqQueryToMethod_RefusesJoin()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            internal class C
            {
                public IEnumerable<string> M(IEnumerable<Order> orders, IEnumerable<Customer> customers)
                    => from o in orders join c in customers on o.CustomerId equals c.Id select c.Name;
            }

            internal class Order { public int CustomerId; }
            internal class Customer { public int Id; public string Name = ""; }
            """;

        var error = await RefusalAsync(source, "linq_query_to_method", 7);
        Assert.Contains("join", error);
    }

    [Fact]
    public async Task LinqMethodToQuery_TranslatesTheChainForm()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            internal class C
            {
                public IEnumerable<string> M(IEnumerable<Order> orders)
                    => orders.Where(o => o.Total > 10).OrderBy(o => o.Id).Select(o => o.Name);
            }

            internal class Order { public int Id; public int Total; public string Name = ""; }
            """;

        using var workspace = await ApplyAsync(source, "linq_method_to_query", 7);

        var text = workspace.Read("C.cs");
        Assert.Contains("from o in orders", text);
        Assert.Contains("where o.Total > 10", text);
        Assert.Contains("orderby o.Id", text);
        Assert.Contains("select o.Name", text);
        Assert.DoesNotContain(".Where(", text);
    }

    [Fact]
    public async Task ConvertToExtensionBlock_RewritesTheReceiverOnceAndKeepsTheBody()
    {
        const string source = """
            internal static class Extensions
            {
                public static int Words(this string receiver)
                {
                    return receiver.Split(' ').Length;
                }
            }
            """;

        using var workspace = await ApplyAsync(source, "convert_to_extension_block", 3);

        var text = workspace.Read("C.cs");
        Assert.Contains("extension(string receiver)", text);
        Assert.Contains("public int Words()", text);
        Assert.Contains("receiver.Split(' ')", text);
        Assert.DoesNotContain("this string", text);
    }

    [Fact]
    public async Task ConvertToExtensionBlock_RefusesAClassWithoutExtensionMethods()
    {
        const string source = """
            internal static class Helpers
            {
                public static int Twice(int value) => value * 2;
            }
            """;

        var error = await RefusalAsync(source, "convert_to_extension_block", 3);
        Assert.Contains("no extension method", error);
    }
}
