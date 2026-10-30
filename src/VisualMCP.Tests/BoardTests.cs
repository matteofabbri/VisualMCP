using System.Text.Json;
using Snitch.Planning;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The kanban board against the real SQLite store: every test drives the service the tool drives, over
/// the temp solution's own database, then asserts on the cards that came back.
/// </summary>
public sealed class BoardTests
{
    private static (FakeWorkspace Workspace, BoardService Board) NewBoard()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var store = new VisualMcpStore(workspace);
        return (workspace, new BoardService(store));
    }

    [Fact]
    public async Task BoardAdd_CreatesTheCardInTheRequestedColumnWithItsPriority()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var result = await board.AddAsync("Wire docker logs", "Show the last 200 lines", "todo", "high",
            ["docker", "api"], "agent");
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal("todo", json.GetProperty("status").GetString());

        var item = json.GetProperty("item");
        Assert.Equal("Wire docker logs", item.GetProperty("title").GetString());
        Assert.Equal("high", item.GetProperty("priority").GetString());
        Assert.Equal("agent", item.GetProperty("assignee").GetString());
        Assert.Equal(2, item.GetProperty("labels").GetArrayLength());
        Assert.Equal("docker", item.GetProperty("labels")[0].GetString());
    }

    [Fact]
    public async Task BoardMove_ChangesColumnAndRecordsHistory()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var added = ResultAssert.Json(await board.AddAsync("Ship it", null, "todo", null, null, null));
        var id = added.GetProperty("id").GetInt32();

        var moved = await board.MoveAsync(id, "in_progress", position: 0);
        ResultAssert.Succeeded(moved);
        Assert.Equal("in_progress", ResultAssert.Json(moved).GetProperty("to").GetString());

        var shown = ResultAssert.Json(await board.ShowAsync(id));
        Assert.Equal("in_progress", shown.GetProperty("item").GetProperty("status").GetString());

        var history = shown.GetProperty("history");
        Assert.Contains(history.EnumerateArray(),
            entry => entry.GetProperty("field").GetString() == "status" &&
                     entry.GetProperty("to").GetString() == "in_progress");
    }

    [Fact]
    public async Task BoardBoard_GroupsTheCardsIntoTheFiveColumns()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.AddAsync("A", null, "backlog", null, null, null);
        await board.AddAsync("B", null, "todo", null, null, null);
        await board.AddAsync("C", null, "todo", null, null, null);
        await board.AddAsync("D", null, "done", null, null, null);

        var boardView = ResultAssert.Json(await board.BoardAsync());
        var columns = boardView.GetProperty("columns");

        Assert.Equal(BoardService.Statuses.Length, columns.GetArrayLength());
        Assert.Equal("backlog", columns[0].GetProperty("status").GetString());
        Assert.Equal(1, columns[0].GetProperty("count").GetInt32());

        var todo = columns[1];
        Assert.Equal("todo", todo.GetProperty("status").GetString());
        Assert.Equal(2, todo.GetProperty("count").GetInt32());

        Assert.Equal(4, boardView.GetProperty("itemCount").GetInt32());
        Assert.Equal(25.0, boardView.GetProperty("doneShare").GetDouble());
    }

    [Fact]
    public async Task BoardComment_KeepsEveryCommentOnTheCard()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var id = ResultAssert.Json(await board.AddAsync("Discuss", null, null, null, null, null)).GetProperty("id").GetInt32();

        await board.CommentAsync(id, "First note", "agent");
        await board.CommentAsync(id, "Second note", "agent");

        var shown = ResultAssert.Json(await board.ShowAsync(id));
        var comments = shown.GetProperty("comments");
        Assert.Equal(2, comments.GetArrayLength());
        Assert.Equal("Second note", comments[1].GetProperty("body").GetString());
        Assert.Equal(2, shown.GetProperty("item").GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task BoardUpdate_ChangesOnlyTheFieldsThatWerePassed()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var id = ResultAssert.Json(await board.AddAsync("Original", "Desc", "todo", "low", ["a"], "me"))
            .GetProperty("id").GetInt32();

        var updated = await board.UpdateAsync(id, title: "Renamed", priority: "high", labels: null, description: null, assignee: null);
        ResultAssert.Succeeded(updated);

        var item = ResultAssert.Json(updated).GetProperty("item");
        Assert.Equal("Renamed", item.GetProperty("title").GetString());
        Assert.Equal("high", item.GetProperty("priority").GetString());
        Assert.Equal("Desc", item.GetProperty("description").GetString());
        Assert.Equal("me", item.GetProperty("assignee").GetString());
        Assert.Equal("a", item.GetProperty("labels")[0].GetString());
    }

    [Fact]
    public async Task BoardList_FiltersByColumnAndText()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.AddAsync("Docker logs", "tail", "todo", null, null, null);
        await board.AddAsync("Git squash", "history", "todo", null, null, null);
        await board.AddAsync("Docker run", null, "done", null, null, null);

        var everythingInTodo = ResultAssert.Json(await board.ListAsync("todo", null, null, null, null));
        Assert.Equal(2, everythingInTodo.GetProperty("count").GetInt32());

        var docker = ResultAssert.Json(await board.ListAsync(null, null, null, null, "docker"));
        Assert.Equal(2, docker.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task BoardDelete_RemovesTheCardAndItsComments()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var id = ResultAssert.Json(await board.AddAsync("Temp", null, null, null, null, null)).GetProperty("id").GetInt32();
        await board.CommentAsync(id, "note", null);

        ResultAssert.Succeeded(await board.DeleteAsync(id));

        var shown = ResultAssert.Json(await board.ShowAsync(id));
        Assert.StartsWith("No board item", shown.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Board_RejectsAnUnknownColumnOrPriority()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        Assert.Contains("is not a column", ResultAssert.Error(await board.AddAsync("X", null, "shipped", null, null, null)));
        Assert.Contains("is not a priority", ResultAssert.Error(await board.AddAsync("X", null, "todo", "urgent", null, null)));
    }

    [Fact]
    public async Task Board_SurvivesANewServiceInstance()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var id = ResultAssert.Json(await board.AddAsync("Persisted", "still here", "review", null, null, null))
            .GetProperty("id").GetInt32();

        // A second store+service over the same folder is what a server restart amounts to.
        var reopened = new BoardService(new VisualMcpStore(workspace));
        var shown = ResultAssert.Json(await reopened.ShowAsync(id));

        Assert.Equal("Persisted", shown.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal("review", shown.GetProperty("item").GetProperty("status").GetString());
    }

    [Fact]
    public async Task BoardImport_IsIdempotentOnTheTitleAndReportsBadCards()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var cards = new[]
        {
            new CardDraft { Title = "Debugger", Status = "todo", Priority = "high", Labels = ["engine"], Description = "DAP client" },
            new CardDraft { Title = "XAML layer", Status = "todo" },
            new CardDraft { Title = "", Status = "todo" },
            new CardDraft { Title = "Bad column", Status = "shipped" },
        };

        var first = ResultAssert.Json(await board.ImportAsync(cards, null));
        Assert.Equal(2, first.GetProperty("imported").GetInt32());
        Assert.Equal(2, first.GetProperty("failed").GetInt32());

        // The same list again: nothing is duplicated, both cards are already there.
        var second = ResultAssert.Json(await board.ImportAsync(cards, null));
        Assert.Equal(0, second.GetProperty("imported").GetInt32());
        Assert.Equal(2, second.GetProperty("skipped").GetInt32());

        var all = ResultAssert.Json(await board.ListAsync(null, null, null, null, null));
        Assert.Equal(2, all.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task BoardImport_UpdatePolicyAppliesTheDraftFields()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.AddAsync("Ship it", "old", "todo", "low", null, null);

        var updated = ResultAssert.Json(await board.ImportAsync(
            [new CardDraft { Title = "Ship it", Status = "in_progress", Priority = "highest", Description = "new" }],
            "update"));

        Assert.Equal(1, updated.GetProperty("updated").GetInt32());
        Assert.Equal(0, updated.GetProperty("imported").GetInt32());

        var card = ResultAssert.Json(await board.ListAsync("in_progress", null, null, null, null));
        var item = card.GetProperty("items")[0];
        Assert.Equal("highest", item.GetProperty("priority").GetString());
        Assert.Equal("new", item.GetProperty("description").GetString());
    }

    [Fact]
    public async Task BoardExport_WritesTheColumnsAsMarkdown()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.AddAsync("Debugger", "A DAP client over netcoredbg", "todo", "high", ["engine"], "agent");
        await board.AddAsync("XAML", null, "backlog", null, null, null);

        var export = ResultAssert.Json(await board.ExportAsync(null));
        var markdown = export.GetProperty("markdown").GetString()!;

        Assert.Contains("## todo (1)", markdown);
        Assert.Contains("## backlog (1)", markdown);
        Assert.Contains("**#1** Debugger — high, agent — `engine`", markdown);
        Assert.Contains("  A DAP client over netcoredbg", markdown);
        Assert.Contains("_Empty._", markdown);

        var onlyTodo = ResultAssert.Json(await board.ExportAsync("todo"));
        Assert.DoesNotContain("## backlog", onlyTodo.GetProperty("markdown").GetString());
    }
}
