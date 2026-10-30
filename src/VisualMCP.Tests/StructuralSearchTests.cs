using Snitch.Languages.CSharp.Search;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Structural Search and Replace: a pattern is matched on the syntax tree with <c>$hole$</c>
/// placeholders, so comments and string contents are never mistaken for code, a hole can stand for an
/// arbitrary subtree, and a rewrite goes through the same compiler gate as every refactoring.
/// </summary>
public sealed class StructuralSearchTests
{
    private const string Logger = """
        using System;

        public static class Logger
        {
            public static void Log(object message) => Console.WriteLine(message);
        }
        """;

    private static FakeWorkspace Workspace(string source) =>
        FakeWorkspace.Create(("Logger.cs", Logger), ("Probe.cs", source));

    private static StructuralSearchService Service(FakeWorkspace workspace) => new(workspace);

    private const string Calls = """
        using System;

        public class Probe
        {
            public void Run()
            {
                Console.WriteLine("a");
                Console.WriteLine(42);
            }
        }
        """;

    [Fact]
    public async Task Search_binds_the_hole_of_every_match()
    {
        using var workspace = Workspace(Calls);

        var result = await Service(workspace).Execute(
            "Console.WriteLine($argument$)", null, "document", workspace.PathOf("Probe.cs"), null,
            dryRun: true, maxResults: 100);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("matchCount").GetInt32());

        var arguments = json.GetProperty("matches").EnumerateArray()
            .Select(match => match.GetProperty("bindings").GetProperty("$argument$").GetString())
            .ToList();

        Assert.Contains("\"a\"", arguments);
        Assert.Contains("42", arguments);
    }

    [Fact]
    public async Task Search_ignores_comments_and_string_literals()
    {
        using var workspace = Workspace("""
            using System;

            public class Probe
            {
                // Console.WriteLine(1);
                public string Text = "Console.WriteLine(2);";
            }
            """);

        var result = await Service(workspace).Execute(
            "Console.WriteLine($argument$)", null, "document", workspace.PathOf("Probe.cs"), null,
            dryRun: true, maxResults: 100);
        ResultAssert.Succeeded(result);

        Assert.Equal(0, ResultAssert.Json(result).GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task Replace_rewrites_every_match_and_writes_the_file()
    {
        using var workspace = Workspace(Calls);

        var result = await Service(workspace).Execute(
            "Console.WriteLine($argument$);", "Logger.Log($argument$);", "document",
            workspace.PathOf("Probe.cs"), null, dryRun: false, maxResults: 100);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.Equal(2, json.GetProperty("matchCount").GetInt32());

        var text = workspace.Read("Probe.cs");
        Assert.True(text.Contains("Logger.Log(\"a\");"), text);
        Assert.True(text.Contains("Logger.Log(42);"), text);
        Assert.DoesNotContain("Console.WriteLine(", text);
    }

    [Fact]
    public async Task Replace_dry_run_leaves_the_file_alone()
    {
        using var workspace = Workspace(Calls);

        var result = await Service(workspace).Execute(
            "Console.WriteLine($argument$);", "Logger.Log($argument$);", "solution", null, null,
            dryRun: true, maxResults: 100);
        ResultAssert.Succeeded(result);

        Assert.False(ResultAssert.Json(result).GetProperty("applied").GetBoolean());
        Assert.Contains("Console.WriteLine(\"a\");", workspace.Read("Probe.cs"));
    }

    [Fact]
    public async Task Replace_refuses_a_hole_the_pattern_does_not_bind()
    {
        using var workspace = Workspace(Calls);

        var result = await Service(workspace).Execute(
            "Console.WriteLine($argument$);", "Logger.Log($missing$);", "solution", null, null,
            dryRun: true, maxResults: 100);

        Assert.Contains("$missing$", ResultAssert.Error(result));
    }

    [Fact]
    public async Task A_hole_matches_an_arbitrary_subtree()
    {
        using var workspace = Workspace("""
            public class Probe
            {
                public string Run(int a, int b) => (a + b).ToString();
            }
            """);

        var result = await Service(workspace).Execute(
            "$value$.ToString()", null, "solution", null, null, dryRun: true, maxResults: 100);
        ResultAssert.Succeeded(result);

        var match = Assert.Single(ResultAssert.Json(result).GetProperty("matches").EnumerateArray());
        Assert.Equal("(a + b)", match.GetProperty("bindings").GetProperty("$value$").GetString());
    }

    [Fact]
    public async Task A_statement_hole_binds_a_block()
    {
        using var workspace = Workspace("""
            public class Probe
            {
                private bool _flag;
                private void Do() { }

                public void Run()
                {
                    if (_flag)
                    {
                        Do();
                    }
                }
            }
            """);

        var result = await Service(workspace).Execute(
            "if ($condition$) $body$;", null, "solution", null, null, dryRun: true, maxResults: 100);
        ResultAssert.Succeeded(result);

        var match = Assert.Single(ResultAssert.Json(result).GetProperty("matches").EnumerateArray());
        Assert.Equal("_flag", match.GetProperty("bindings").GetProperty("$condition$").GetString());
        Assert.Contains("Do();", match.GetProperty("bindings").GetProperty("$body$").GetString());
    }

    [Fact]
    public async Task Replace_with_a_statement_hole_rewrites_a_block()
    {
        using var workspace = Workspace("""
            public class Probe
            {
                private bool _flag;
                private void Do() { }

                public void Run()
                {
                    if (_flag)
                    {
                        Do();
                    }
                }
            }
            """);

        var result = await Service(workspace).Execute(
            "if ($condition$) $body$;", "if (!$condition$) $body$;", "document",
            workspace.PathOf("Probe.cs"), null, dryRun: false, maxResults: 100);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.Contains("if (!_flag)", text);
        Assert.Contains("Do();", text);
    }

    [Fact]
    public async Task Nested_matches_are_rewritten_once()
    {
        using var workspace = Workspace("""
            using System;

            public class Probe
            {
                public void Run() => Console.WriteLine(Console.WriteLine(1));
            }
            """);

        var result = await Service(workspace).Execute(
            "Console.WriteLine($argument$)", null, "document", workspace.PathOf("Probe.cs"), null,
            dryRun: true, maxResults: 100);
        ResultAssert.Succeeded(result);

        // The outer call and the inner one both match; applying both would edit a node the outer
        // replacement already removed, so only the outermost is reported.
        Assert.Equal(1, ResultAssert.Json(result).GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task The_compiler_gate_refuses_a_replacement_that_would_not_compile()
    {
        using var workspace = Workspace("""
            public class Probe
            {
                public int Run(int value) => value * 2;
            }
            """);

        var result = await Service(workspace).Execute(
            "$x$ * 2", "$x$ * unknownSymbol", "solution", null, null, dryRun: false, maxResults: 100);

        Assert.Contains("Refused", ResultAssert.Error(result));
        Assert.Contains("value * 2", workspace.Read("Probe.cs"));
    }

    [Fact]
    public async Task Replacement_is_reindented_to_the_match_column()
    {
        using var workspace = Workspace("""
            public class Probe
            {
                private bool _flag;
                private void Do() { }
                private void Before() { }

                public void Run()
                {
                    if (_flag)
                    {
                        Do();
                    }
                }
            }
            """);

        var result = await Service(workspace).Execute(
            "if ($condition$) $body$;", "if ($condition$) { Before(); $body$; }", "document",
            workspace.PathOf("Probe.cs"), null, dryRun: false, maxResults: 100);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("Probe.cs");
        Assert.Contains("Before();", text);
        Assert.Contains("        Do();", text);
    }
}
