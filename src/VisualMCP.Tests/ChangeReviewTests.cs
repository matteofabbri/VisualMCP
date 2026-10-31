using Snitch.Collaboration;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The review brief against a real diff: file classification, the checklist a reviewer starts from,
/// the TODO markers the change introduces with their new-file lines, and the maxFiles cap.
/// </summary>
public sealed class ChangeReviewTests
{
    private static ChangeReviewService Service(TestGitRepo repo) => new(repo.Workspace, inspections: null);

    [Fact]
    public async Task Review_ClassifiesTheChange_AndBuildsTheChecklist()
    {
        using var repo = new TestGitRepo();
        repo.Write("src/App.cs", "class A { }\n");
        repo.Write("tests/AppTests.cs", "class T { }\n");
        repo.Write("README.md", "# x\n");
        var baseline = repo.Commit("baseline");

        repo.Write("src/App.cs", "class A\n{\n    // TODO: fix this\n    void M() { }\n}\n");
        repo.Write("README.md", "# x\nmore\n");
        File.Delete(Path.Combine(repo.Directory, "tests", "AppTests.cs"));

        var result = await Service(repo).ReviewAsync(baseline, pathFilter: null, maxFiles: 100,
            includeInspections: false, repo.Directory, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(3, json.GetProperty("fileCount").GetInt32());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("source").GetInt32());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("docs").GetInt32());
        Assert.Equal(1, json.GetProperty("summary").GetProperty("deleted").GetInt32());

        var kinds = json.GetProperty("checklist").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString()).ToList();
        Assert.Contains("deleted-test", kinds);
        Assert.Contains("new-todo", kinds);

        var todo = Assert.Single(json.GetProperty("todos").EnumerateArray());
        Assert.Equal("src/App.cs", todo.GetProperty("file").GetString());
        Assert.Equal("TODO", todo.GetProperty("marker").GetString());
        Assert.Equal(3, todo.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Review_SourceWithoutTests_IsAWarning()
    {
        using var repo = new TestGitRepo();
        repo.Write("src/App.cs", "class A { }\n");
        var baseline = repo.Commit("baseline");

        repo.Write("src/App.cs", "class A { int X; }\n");

        var json = ResultAssert.Json(await Service(repo).ReviewAsync(baseline, null, 100, false, repo.Directory, 30));

        var kinds = json.GetProperty("checklist").EnumerateArray()
            .Select(entry => entry.GetProperty("kind").GetString()).ToList();
        Assert.Contains("no-test-change", kinds);
        Assert.DoesNotContain("deleted-test", kinds);
    }

    [Fact]
    public async Task Review_EmptyDiff_SaysThereIsNothingToReview()
    {
        using var repo = new TestGitRepo();
        repo.Write("src/App.cs", "class A { }\n");
        var baseline = repo.Commit("baseline");

        var json = ResultAssert.Json(await Service(repo).ReviewAsync(baseline, null, 100, false, repo.Directory, 30));

        Assert.Equal(0, json.GetProperty("fileCount").GetInt32());
        Assert.Contains("Nothing to review", json.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Review_FollowsARenameToTheNewPath()
    {
        using var repo = new TestGitRepo();
        var body = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"// line {i}")) + "\nclass Old { }\n";
        repo.Write("src/Old.cs", body);
        var baseline = repo.Commit("baseline");

        Assert.Equal(0, repo.Run("mv", "src/Old.cs", "src/New.cs").exitCode);
        // Keep most of the body so git records a rename, and change one line so the numstat row is real.
        repo.Write("src/New.cs", body.Replace("// line 4", "// line four"));

        var json = ResultAssert.Json(await Service(repo).ReviewAsync(baseline, null, 100, false, repo.Directory, 30));

        Assert.Equal(1, json.GetProperty("fileCount").GetInt32());
        var file = json.GetProperty("files")[0];
        Assert.Equal("src/New.cs", file.GetProperty("path").GetString());
        Assert.Equal("renamed", file.GetProperty("status").GetString());
        Assert.Equal(1, file.GetProperty("additions").GetInt32());
        Assert.Equal(1, file.GetProperty("deletions").GetInt32());
    }

    [Fact]
    public async Task Review_MaxFiles_CapsTheListButNotTheSummary()
    {
        using var repo = new TestGitRepo();
        repo.Write("a.cs", "class A { }\n");
        repo.Write("b.cs", "class B { }\n");
        repo.Write("c.cs", "class C { }\n");
        var baseline = repo.Commit("baseline");

        repo.Write("a.cs", "class A { int X; }\n");
        repo.Write("b.cs", "class B { int X; }\n");
        repo.Write("c.cs", "class C { int X; }\n");

        var json = ResultAssert.Json(await Service(repo).ReviewAsync(baseline, null, maxFiles: 2, false, repo.Directory, 30));

        Assert.Equal(3, json.GetProperty("fileCount").GetInt32());
        Assert.Equal(2, json.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Review_PathFilter_NarrowsTheChange()
    {
        using var repo = new TestGitRepo();
        repo.Write("src/App.cs", "class A { }\n");
        repo.Write("docs/readme.md", "# x\n");
        var baseline = repo.Commit("baseline");

        repo.Write("src/App.cs", "class A { int X; }\n");
        repo.Write("docs/readme.md", "# x\nmore\n");

        var json = ResultAssert.Json(await Service(repo).ReviewAsync(baseline, "docs", 100, false, repo.Directory, 30));

        Assert.Equal(1, json.GetProperty("fileCount").GetInt32());
        Assert.Equal("docs/readme.md", json.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal("docs", json.GetProperty("files")[0].GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("src/App.cs", "source")]
    [InlineData("tests/AppTests.cs", "test")]
    [InlineData("src/App.Tests/AppTests.cs", "test")]
    [InlineData("src/App.razor", "ui")]
    [InlineData("README.md", "docs")]
    [InlineData("Directory.Build.props", "config")]
    [InlineData("scripts/build.ps1", "other")]
    public void KindOf_GroupsFilesTheWayAReviewerDoes(string path, string expected) =>
        Assert.Equal(expected, ChangeReviewService.KindOf(path));

    [Fact]
    public void ParseTodos_UsesTheNewFileLineNumbers()
    {
        const string diff = """
            +++ b/src/App.cs
            @@ -1,2 +1,4 @@
             class A
             {
            +    // FIXME: later
            +    void M() { }
             }
            """;

        var todos = ChangeReviewService.ParseTodos(diff, out var files);

        var todo = Assert.Single(todos);
        Assert.Equal("src/App.cs", todo.File);
        Assert.Equal(3, todo.Line);
        Assert.Equal("FIXME", todo.Marker);
        Assert.Equal(new[] { "src/App.cs" }, files);
    }
}
