using Snitch.Languages.CSharp.Refactoring;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// CodeMaid's Format Document rules: comment reflow to a width, blank-line normalisation (no runs, none
/// after an opening brace or before a closing one), and joining a line range — refused when the join
/// would change the token stream, which is how a multi-line string or a comment is protected.
/// </summary>
public sealed class LayoutCodeTests
{
    private const string LongComment = """
        internal class C
        {
            // This is a very long comment that goes on and on past the configured width and has to be wrapped so that it stays readable.
            public void M()
            {
            }
        }
        """;

    private const string BlankLines = """
        internal class C
        {

            public int M()
            {


                return 1;
            }

        }
        """;

    [Fact]
    public async Task Comments_AreWrappedToTheWidth()
    {
        var workspace = FakeWorkspace.Create(("C.cs", LongComment));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "comments", 60,
            null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var commentLines = File.ReadAllLines(workspace.PathOf("C.cs"))
            .Where(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        Assert.True(commentLines.Count > 1, "the long comment should occupy several lines");
        Assert.All(commentLines, line => Assert.True(line.Length <= 60, $"'{line}' is wider than 60"));

        var joined = string.Join(" ", commentLines.Select(line => line.Trim()[2..].Trim()));
        Assert.Contains("This is a very long comment", joined);
        Assert.Contains("stays readable.", joined);
        Assert.True(ResultAssert.Json(result).GetProperty("commentsWrapped").GetInt32() > 0);
    }

    [Fact]
    public async Task BlankLines_AreCollapsedAndTrimmedAtTheBraces()
    {
        var workspace = FakeWorkspace.Create(("C.cs", BlankLines));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "blank_lines", 120,
            null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.DoesNotContain("\n\n\n", text);
        Assert.DoesNotContain("{\n\n", text);
        Assert.DoesNotContain("\n\n}", text);
        Assert.Contains("public int M()", text);
        Assert.True(ResultAssert.Json(result).GetProperty("blankLinesRemoved").GetInt32() >= 4);
    }

    [Fact]
    public async Task Join_LinesTurnsAStatementIntoOneLine()
    {
        const string source = """
            internal class C
            {
                public int M()
                {
                    var total = 1 +
                        2;
                    return total;
                }
            }
            """;

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "join", 120,
            fromLine: 5, toLine: 6, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("        var total = 1 + 2;", workspace.Read("C.cs"));
        Assert.Equal(1, ResultAssert.Json(result).GetProperty("linesJoined").GetInt32());
    }

    [Fact]
    public async Task Join_IsRefusedWhenItWouldChangeTheTokens()
    {
        const string source = "internal class C\n{\n    public string M()\n    {\n        return @\"a\n\tb\";\n    }\n}\n";

        var workspace = FakeWorkspace.Create(("C.cs", source));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "join", 120,
            fromLine: 5, toLine: 6, dryRun: true);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesModified").GetInt32());
        var refused = json.GetProperty("refused").EnumerateArray().ToList();
        Assert.Single(refused);
        Assert.Contains("tokens", refused[0].GetProperty("reason").GetString());
        Assert.Equal(source, workspace.Read("C.cs"));
    }

    [Fact]
    public async Task DryRun_ReportsTheChangeWithoutWritingIt()
    {
        var workspace = FakeWorkspace.Create(("C.cs", BlankLines));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "blank_lines", 120,
            null, null, dryRun: true);
        ResultAssert.Succeeded(result);

        Assert.Equal(1, ResultAssert.Json(result).GetProperty("filesModified").GetInt32());
        Assert.Equal(BlankLines, workspace.Read("C.cs"));
    }

    [Fact]
    public async Task Join_NeedsARange()
    {
        var workspace = FakeWorkspace.Create(("C.cs", LongComment));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "join", 120,
            null, null, dryRun: true);
        Assert.Contains("fromLine and toLine", ResultAssert.Error(result));
    }

    [Fact]
    public async Task AnUnknownRuleIsRefusedWithTheCatalogue()
    {
        var workspace = FakeWorkspace.Create(("C.cs", LongComment));
        using var scope = workspace;
        var layout = new LayoutCodeService(workspace);

        var result = await layout.Apply("document", workspace.PathOf("C.cs"), null, "teleport", 120,
            null, null, dryRun: true);

        Assert.Contains("teleport", ResultAssert.Error(result));
        Assert.Contains("blank_lines", ResultAssert.Json(result).GetProperty("supported").EnumerateArray()
            .Select(rule => rule.GetString()).ToList());
    }
}
