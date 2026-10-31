using System.Text.Json;
using Snitch.Planning;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The memory lane of the board: a topic has one current version, writing it again supersedes instead of
/// duplicating, recall finds it, the brief carries the pinned ones, and a plan is a card with a tickable
/// checklist — not a Markdown file.
/// </summary>
public sealed class BoardMemoryTests
{
    private static (FakeWorkspace Workspace, BoardService Board) NewBoard()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        return (workspace, new BoardService(new VisualMcpStore(workspace)));
    }

    [Fact]
    public async Task RememberWritesTheTopicAndRecallReadsItBack()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var result = ResultAssert.Json(await board.RememberAsync(
            "sqlite-ef-linq", "The stores read through EF Core.", "decision", ["storage"], pinned: true, source: null));

        Assert.False(result.GetProperty("Unchanged").GetBoolean());
        Assert.Equal("sqlite-ef-linq", result.GetProperty("Topic").GetString());

        var recalled = ResultAssert.Json(await board.RecallAsync("sqlite-ef-linq", null, null, null, false, 10));
        Assert.Equal(1, recalled.GetProperty("count").GetInt32());

        var note = recalled.GetProperty("items")[0];
        Assert.Equal("decision", note.GetProperty("kind").GetString());
        Assert.Equal("The stores read through EF Core.", note.GetProperty("body").GetString());
        Assert.True(note.GetProperty("pinned").GetBoolean());
        Assert.Equal("storage", note.GetProperty("labels")[0].GetString());
    }

    [Fact]
    public async Task TheSameTopicSupersedesInsteadOfDuplicating()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var first = ResultAssert.Json(await board.RememberAsync("routing", "The board is the plan.", "fact", null, false, null));
        var second = ResultAssert.Json(await board.RememberAsync("routing", "The board is the plan; files are history.", "fact", null, false, null));

        Assert.True(second.GetProperty("Replaced").GetBoolean());
        Assert.Equal(first.GetProperty("Id").GetInt32(), second.GetProperty("PreviousId").GetInt32());

        var recalled = ResultAssert.Json(await board.RecallAsync("routing", null, null, null, false, 10));
        Assert.Equal(1, recalled.GetProperty("count").GetInt32());
        Assert.Equal("The board is the plan; files are history.",
            recalled.GetProperty("items")[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task TheSameBodyTwiceIsANoOp()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.RememberAsync("routing", "Same fact.", "fact", null, false, null);
        var again = ResultAssert.Json(await board.RememberAsync("routing", "Same fact.", "fact", null, false, null));

        Assert.True(again.GetProperty("Unchanged").GetBoolean());
        Assert.False(again.GetProperty("Remembered").GetBoolean());
    }

    [Fact]
    public async Task RecallSearchesTopicAndBody()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.RememberAsync("sqlite-ef-linq", "Letture via LINQ.", "fact", null, false, null);
        await board.RememberAsync("hook-routing", "The hook denies shell probes.", "constraint", null, false, null);

        var byBody = ResultAssert.Json(await board.RecallAsync(null, "shell probes", null, null, false, 10));
        Assert.Equal(1, byBody.GetProperty("count").GetInt32());
        Assert.Equal("hook-routing", byBody.GetProperty("items")[0].GetProperty("topic").GetString());

        var byKind = ResultAssert.Json(await board.RecallAsync(null, null, "constraint", null, false, 10));
        Assert.Equal(1, byKind.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task ForgetRetiresTheTopicButKeepsHistory()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.RememberAsync("obsolete", "Old fact.", "fact", null, false, null);

        var forgotten = ResultAssert.Json(await board.ForgetAsync("obsolete"));
        Assert.True(forgotten.GetProperty("forgotten").GetBoolean());

        var recalled = ResultAssert.Json(await board.RecallAsync("obsolete", null, null, null, false, 10));
        Assert.Equal(0, recalled.GetProperty("count").GetInt32());

        // Writing the topic again starts a new current version.
        await board.RememberAsync("obsolete", "New fact.", "fact", null, false, null);
        var again = ResultAssert.Json(await board.RecallAsync("obsolete", null, null, null, false, 10));
        Assert.Equal(1, again.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TheBriefCarriesThePinnedNotes()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.RememberAsync("engine-rule", "Never take a lock.", "constraint", null, pinned: true, null);
        await board.RememberAsync("unpinned", "A detail.", "fact", null, false, null);

        var brief = ResultAssert.Json(await board.BriefAsync(14));

        var memory = brief.GetProperty("memory");
        Assert.Equal(2, memory.GetProperty("count").GetInt32());
        Assert.Equal(1, memory.GetProperty("pinned").GetInt32());
        Assert.Contains("engine-rule", memory.GetProperty("recent").EnumerateArray()
            .Select(entry => entry.GetProperty("topic").GetString()));
    }

    [Fact]
    public async Task APlanIsACardWithATickableChecklist()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var added = ResultAssert.Json(await board.AddAsync(
            "Wire the memory lane", null, "in_progress", "high", ["board"], null,
            ["Add the table", "Write the tools", "Run the suite"]));
        var id = added.GetProperty("id").GetInt32();

        Assert.Equal(3, added.GetProperty("stepCount").GetInt32());
        Assert.Contains("- [ ] Add the table", added.GetProperty("item").GetProperty("description").GetString());

        var ticked = ResultAssert.Json(await board.TickPlanAsync(id, [1, 2], null));
        Assert.Equal(2, ticked.GetProperty("doneCount").GetInt32());
        Assert.Equal(1, ticked.GetProperty("remaining").GetInt32());
        Assert.False(ticked.GetProperty("allDone").GetBoolean());
        Assert.Contains("- [x] Add the table", ticked.GetProperty("content").GetString());
        Assert.Contains("- [ ] Run the suite", ticked.GetProperty("content").GetString());
    }

    [Fact]
    public async Task TickingACardWithoutAChecklistIsRefused()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        var id = ResultAssert.Json(await board.AddAsync("No plan", "just a description", null, null, null, null))
            .GetProperty("id").GetInt32();

        Assert.Contains("no checklist", ResultAssert.Error(await board.TickPlanAsync(id, [1], null)));
    }

    [Fact]
    public async Task TheSummaryCarriesTheMemoryCount()
    {
        var (workspace, board) = NewBoard();
        using var _ = workspace;

        await board.RememberAsync("pinned-rule", "Read me first.", "constraint", null, pinned: true, null);

        var summary = await board.SummaryAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.MemoryCount);
        Assert.Contains("pinned-rule", summary.MemoryPinned!);
    }
}
